/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using System.Text;
using JamesFrowen.CSP.Alloc;
using JamesFrowen.DeltaSnapshot;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Assertions;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// Controls all objects on client
    /// </summary>
    internal class ClientManager : ITickNotifyTracker
    {
        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager");
        private static readonly ILogger verbose = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager_Verbose", LogType.Exception);
        private static readonly ProfilerMarker simulateMarker = new ProfilerMarker("Client.Simulate");
        private static readonly ProfilerMarker resimulateMarker = new ProfilerMarker("Client.Resimulate");

        private static StringBuilder _debugBuilder = new StringBuilder();

        private readonly TickRunner _tickRunner;
        private readonly IPredictionSimulation _simulation;
        private readonly PredictionCollection _behaviours;
        private readonly INetworkPlayer _clientPlayer;
        private readonly ClientTickRunner clientTickRunner;
        private readonly int _bufferSize;
        private readonly IAllocator _allocator;
        private readonly WorldSnapshot _worldSnapshot;

        /// <summary>Time used for physics, includes resimulation time. Driven by <see cref="_time"/></summary>
        private readonly PredictionTime _time;
        private readonly NetworkWorld world;
        private readonly ClientInterpolation _clientInterpolation;
        private int? lastReceivedTick;
        private bool _needResimulate;
        private const int MAX_INPUT_PER_PACKET = 8;
        private int? ackedInput;

        public bool ReadyForWorldState = false;

        int? ITickNotifyTracker.LastAckedTick { get => ackedInput; }
        void ITickNotifyTracker.SetLastAcked(int tick)
        {
            if (ackedInput.HasValue)
                ackedInput = Math.Max(ackedInput.Value, tick);
            else
                ackedInput = tick;
        }
        void ITickNotifyTracker.ClearLastAcked()
        {
            ackedInput = null;
        }
        public PredictionCollection Behaviours => _behaviours;

        public int Debug_ServerTick => lastReceivedTick.GetValueOrDefault();

        //delta snapshot
        private readonly NullableRingBuffer<WorldStateCopy> _worldStateCopy;
        private readonly DeltaSnapshotWriter _deltaSnapshot;


        public ClientManager(
            IPredictionSimulation simulation,
            ClientTickRunner clientTickRunner,
            PredictionTime time,
            NetworkWorld world,
            INetworkPlayer clientPlayer,
            MessageHandler messageHandler,
            IAllocator allocator,
            int bufferSize = PredictionManager.DEFAULT_BUFFER_SIZE
            )
        {
            _bufferSize = bufferSize;
            _allocator = allocator;
            _worldSnapshot = new WorldSnapshot(_allocator, bufferSize);
            _tickRunner = clientTickRunner;
            _time = time;
            _behaviours = new PredictionCollection(_time);
            _simulation = simulation;
            _clientPlayer = clientPlayer;
            this.clientTickRunner = clientTickRunner;
            this.clientTickRunner.OnTick += Tick;
            this.clientTickRunner.OnTickSkip += OnTickSkip;

            _clientInterpolation = new ClientInterpolation(_time);
            Behaviours.Add(_clientInterpolation);

            messageHandler.RegisterHandler<DeltaWorldState>(ReceiveDeltaWorldState);
            this.world = world;
            world.onSpawn += OnSpawn;
            world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (var item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }

            _worldStateCopy = new NullableRingBuffer<WorldStateCopy>(bufferSize);
            for (var i = 0; i < bufferSize; i++)
            {
                _worldStateCopy.Set(i, new WorldStateCopy());
            }
            _deltaSnapshot = new DeltaSnapshotWriter(_allocator);
        }

        private void OnTickSkip()
        {
            if (logger.LogEnabled()) logger.Log($"Tick Skip");

            // clear inputs, start a fresh
            // set to no value so SendInput can handle it as if there are no acks
            ackedInput = null;
        }

        public void OnSpawn(NetworkIdentity identity)
        {
            _behaviours.Add(identity, out var _, out var foundBehaviours);

            var snapshots = GetBehaviourCache<ISnapshotBehaviour>.GetBehaviours(identity);
            if (snapshots.Count == 0)
                return;

            // allocate here so that state can be used before first tick
            // in first tick this state will be copied to the GroupSnapshot for that tick
            var snap = _worldSnapshot.CreateAndAdd(identity, snapshots.ToArray(), _allocator);
            snap.SetActivePtr(_time.Tick);

            foreach (var behaviour in foundBehaviours)
            {
                if (logger.LogEnabled()) logger.Log($"Spawned (netId:{((NetworkBehaviour)behaviour).NetId},comp:{((NetworkBehaviour)behaviour).ComponentIndex}) {behaviour.GetType()}");

                behaviour.ClientSetup(_bufferSize, _clientInterpolation);
            }
        }

        public void OnUnspawn(NetworkIdentity identity)
        {
            _behaviours.Remove(identity, out var _, out var _);
            _worldSnapshot.Remove(identity, false);
        }

        private unsafe void ReceiveDeltaWorldState(INetworkPlayer player, DeltaWorldState msg)
        {
            if (msg.Fragmented)
            {
                player.Send(new DeltaWorldStateFragmentedAck { Tick = msg.Tick });
            }

            if (lastReceivedTick > msg.Tick)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"State out of order, Dropping state for {msg.Tick} (last={lastReceivedTick})");
                return;
            }

            if (msg.TimeScale.HasValue)
            {
                Time.timeScale = msg.TimeScale.Value;
            }
            // else if no value, then reset scale to 1
            else if (Time.timeScale != 1)
            {
                Time.timeScale = 1;
            }

            if (msg.DeltaState.Array != null)
            {
                if (logger.LogEnabled()) logger.Log($"Received delta tick:{msg.Tick} vsTick:{msg.VsTick} Size:{msg.StateIntSize} PayloadSize:{msg.DeltaState.Count}");

                ReadDeltaFromMessage(msg);
                CopyStateForTick(msg.Tick);
            }

            clientTickRunner.OnMessage(msg.Tick, msg.ClientTime);
            _clientInterpolation.OnMessage(msg.Tick);
        }

        private unsafe void ReadDeltaFromMessage(DeltaWorldState msg)
        {
            using (var reader = NetworkReaderPool.GetReader(msg.DeltaState, world))
            {
                var toCopy = _worldStateCopy.GetOrDefault(msg.Tick);
                toCopy.CheckSize(_allocator, msg.StateIntSize);

                if (msg.VsTick.HasValue)
                {
                    var fromCopy = _worldStateCopy.GetOrDefault(msg.VsTick.Value);
                    _deltaSnapshot.ReadDelta(reader, toCopy.IntSize, fromCopy.Ptr, toCopy.Ptr);
                }
                else
                {
                    _deltaSnapshot.ReadDeltaVsZero(reader, toCopy.IntSize, toCopy.Ptr);
                }
            }
        }

        private unsafe void CopyStateForTick(int tick)
        {
            // todo make sure these can be set after, this function shouldn't be dealing with timing, so it should be fine to say we have not received state when it is null
            //_needResimulate = true; done at end after checking changes
            lastReceivedTick = tick;

            var copy = _worldStateCopy.GetOrDefault(tick);
            var readPtr = copy.Ptr;

            var end = copy.Ptr + copy.IntSize;

            var anyChanged = false;
            var lookup = _worldSnapshot.LookUp;
            while (readPtr < end)
            {
                // note: dont need to +1 for readPtr here, becuase group.IntSize includes it
                var header = (IdentitySnapshot.Header*)readPtr;

                if (header->NetId == 0)
                    throw new Exception($"Read netid as 0 at snapshotPosition {end - readPtr}");

                // object might be new? and not in snapshot
                // if so, add it to tick
                if (!lookup.TryGetValue(header->NetId, out var snapshot))
                {
                    logger.LogError($"(TODO FIX THIS) Could not find NetworkIdentity with id={header->NetId}, Stoping ReceiveState");
                    return;
                }

                // we dont need to
                var ptr = snapshot.GetStateAtTick(tick);
                var changed = UnsafeHelper.CopyAndCheckChanged(readPtr, ptr, snapshot.IntSizePerTick);
                anyChanged |= changed;

                if (changed && logger.LogEnabled())
                {
                    logger.Log($"Changed (netId:{header->NetId}) {snapshot.Identity.name}");
                }

                readPtr += snapshot.IntSizePerTick;

                if (verbose.LogEnabled())
                    Verbose_LogBehaviourState(readPtr, snapshot);
            }

            _needResimulate = anyChanged;
        }

        private static unsafe void Verbose_LogBehaviourState(int* writePtr, IdentitySnapshot snap)
        {
            var startPtr = writePtr - snap.IntSizePerTick;
            verbose.Log($"ReadGroup:{snap.IntSizePerTick * 4} bytes, netId:{snap.Identity.NetId}, Object:{snap.Identity.name} Hex:[{*startPtr:X8}]");
            startPtr += 1;

            foreach (var snapshot in snap.Snapshots)
            {
                var netBehaviour = (NetworkBehaviour)snapshot;
                Debug.Assert(netBehaviour.NetId <= 0xffffff);
                Debug.Assert(netBehaviour.ComponentIndex <= 0xff);

                _debugBuilder.Clear();
                for (var j = 0; j < snapshot.AllocationSizeInts; j++)
                {
                    if (j != 0)
                        _debugBuilder.Append(" ");

                    var value = startPtr[j];
                    string intStr;
                    // zero is a common value, so avoid format string
                    if (value == 0)
                        intStr = "00000000";
                    else
                        intStr = value.ToString("X8");
                    _debugBuilder.Append(intStr);
                }
                verbose.Log($"ReadState:{snapshot.AllocationSizeInts * 4} bytes, Type:{snapshot.GetType()} Hex:[{_debugBuilder}]");
                startPtr += snapshot.AllocationSizeInts;
            }
        }

        private unsafe void Resimulate(int from, int to)
        {
            resimulateMarker.Begin();
            try
            {
                if (from > to)
                {
                    logger.LogError($"Cant resimulate because 'from' was after 'to'. From:{from} To:{to}");
                    return;
                }
                if (to - from > _bufferSize)
                {
                    logger.LogError($"Cant resimulate more than BufferSize. From:{from} To:{to}");
                    return;
                }
                if (logger.LogEnabled()) logger.Log($"Resimulate from {from} to {to}");

                var behaviours = _behaviours.GetBehaviours();
                var count = behaviours.Count;

                // call before Resim first, to get snapshot (inside ClientController of state)
                for (var i = 0; i < count; i++)
                    behaviours[i].ClientController.BeforeResimulate();

                // then apply last received, and debug create after image
                _worldSnapshot.SetActivePtr(from - 1);

                for (var i = 0; i < count; i++)
                {
                    var behaviour = behaviours[i];
                    behaviour.AfterStateChanged();

                    if (behaviour is IDebugPredictionAfterImage debug && debug.ShowAfterImage)
                        debug.CreateAfterImage(behaviour.Ptr, new Color(1f, 0.4f, 0f));
                }

                // step forward Applying inputs
                _time.IsResimulation = true;
                for (var tick = from; tick <= to; tick++)
                {
                    Simulate(tick);
                }
                _time.IsResimulation = false;

                for (var i = 0; i < count; i++)
                    behaviours[i].ClientController.AfterResimulate();
            }
            finally
            {
                resimulateMarker.End();
            }
        }

        private void Simulate(int tick)
        {
            simulateMarker.Begin();
            try
            {
                _time.Tick = tick;

                _worldSnapshot.CopyFromPreviousTick(tick);

                var updates = _behaviours.GetUpdates();
                for (var i = 0; i < updates.Count; i++)
                {
                    var update = updates[i];
                    // if behaviour run full tick stuff, otherwise just call fixedupdate
                    if (update is IPredictionBehaviour behaviour)
                        behaviour.ClientController.Simulate(tick);
                    else
                        update.NetworkFixedUpdate();
                }
                _simulation.Simulate(_time.FixedDeltaTime);

                // todo, do we need to do this here on client? (might only be needed before we resimulate)
                var behaviours = _behaviours.GetBehaviours();
                for (var i = 0; i < behaviours.Count; i++)
                {
                    var behaviour = behaviours[i];
                    behaviour.AfterTick();
                }
            }
            finally
            {
                simulateMarker.End();
            }
        }

        internal void Tick(int tick)
        {
            _time.Method = UpdateMethod.NetworkFixed;

            // set lastSim to +1, so if we receive new snapshot, then we sim up to 106 again
            // we only want to step forward 1 tick at a time so we collect inputs, and sim correctly
            // todo: what happens if we do 2 at once, is that really a problem?

            if (_needResimulate)
            {
                // from +1 because we receive N, so we need to simulate n+1
                // sim up to N-1, we do N below when we get new inputs
                Resimulate(lastReceivedTick.Value + 1, tick - 1);
                _needResimulate = false;
            }

            _time.Tick = tick;

            var behaviours = _behaviours.GetBehaviours();
            var count = behaviours.Count;
            for (var i = 0; i < count; i++)
            {
                var behaviour = behaviours[i];
                // get and send inputs
                if (behaviour.UseInputs())
                    behaviour.ClientController.InputTick(tick);
            }

            SendInputs(tick);
            Simulate(tick);
            _time.Method = UpdateMethod.None;
        }

        private void SendInputs(int tick)
        {
            // no value means this is first send
            // for this case we can just send the acked value to tick-1 so that only new input is sent
            // next frame it will send this and next frames inputs like it should normally
            if (ackedInput == null)
                ackedInput = tick - 1;

            if (ReadyForWorldState)
                SendReadyInputs(tick);
            else
                SendNotReadyInputs(tick);
        }

        private void SendReadyInputs(int tick)
        {
            if (logger.LogEnabled()) logger.Log($"sending inputs for {tick}. length: {tick - ackedInput}");

            Debug.Assert(tick > ackedInput, "new input should not have been acked before it was sent");

            var numberOfTicks = tick - ackedInput.Value;
            var length = Math.Min(numberOfTicks, MAX_INPUT_PER_PACKET);
            Assert.IsTrue(1 <= length && length <= 8);

            using (var writer = NetworkWriterPool.GetWriter())
            {
                var behaviours = _behaviours.GetBehaviours();
                var count = behaviours.Count;
                for (var i = 0; i < count; i++)
                {
                    var behaviour = behaviours[i];
                    // get and send inputs
                    if (behaviour.UseInputs())
                    {
                        var nb = (NetworkBehaviour)behaviour;
                        Debug.Assert(nb.HasAuthority);
                        writer.WriteNetworkBehaviour(nb);
                        for (var j = 0; j < length; j++)
                        {
                            var t = tick - j;
                            behaviour.ClientController.WriteInput(writer, t);
                        }
                    }
                }

                var message = new InputState
                {
                    Ready = true,
                    Tick = tick,
                    ClientTime = _time.UnscaledTime,
                    NumberOfInputs = length,
                    Payload = writer.ToArraySegment(),
                };

                var token = TickNotifyToken.GetToken(this, tick);
                _clientPlayer.Send(message, token);
            }
        }

        private void SendNotReadyInputs(int tick)
        {
            var message = new InputState
            {
                Ready = false,
                Tick = tick,
                ClientTime = _time.UnscaledTime,
                NumberOfInputs = default,
                Payload = default,
            };

            var token = TickNotifyToken.GetToken(this, tick);
            _clientPlayer.Send(message, token);
        }
    }
}
