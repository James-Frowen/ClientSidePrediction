/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using JamesFrowen.DeltaSnapshot;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Assertions;

namespace JamesFrowen.CSP
{
    internal class ClientCSP : ITickNotifyTracker
    {
        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager");
        private static readonly ILogger verbose = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager_Verbose", LogType.Exception);
        private static readonly ProfilerMarker simulateMarker = new ProfilerMarker("Client.Simulate");
        private static readonly ProfilerMarker resimulateMarker = new ProfilerMarker("Client.Resimulate");

        private readonly TickRunner _tickRunner;
        private readonly IPredictionSimulation _simulation;
        private readonly PredictionCollection _behaviours;
        private readonly INetworkPlayer _clientPlayer;
        private readonly ClientDeltaSnapshot _clientDeltaSnapshot;
        private readonly ClientTickRunner clientTickRunner;
        private readonly int _bufferSize;

        /// <summary>Time used for physics, includes resimulation time. Driven by <see cref="_time"/></summary>
        private readonly PredictionTime _time;
        private readonly NetworkWorld world;
        private const int MAX_INPUT_PER_PACKET = 8;
        private int? ackedInput;
        private int lastInputTick;

        private int _lastSend;

        public bool ReadyForWorldState = false;
        private int? _lastResimulation;

        private bool NeedResimulation(out int lastReceived)
        {
            var lastChanged = _clientDeltaSnapshot.LastChanges;
            // no changes, no resim
            if (!lastChanged.HasValue)
            {
                lastReceived = default;
                return false;
            }

            lastReceived = lastChanged.Value;

            // never resim, need to resim with latest changes
            if (!_lastResimulation.HasValue)
                return true;

            // new changes
            return lastChanged.Value > _lastResimulation.Value;
        }

        public PredictionCollection Behaviours => _behaviours;

        public ClientCSP(
            IPredictionSimulation simulation,
            ClientTickRunner clientTickRunner,
            PredictionTime time,
            NetworkWorld world,
            INetworkPlayer clientPlayer,
           ClientDeltaSnapshot clientDeltaSnapshot,
            int bufferSize
            )
        {
            _bufferSize = bufferSize;
            _tickRunner = clientTickRunner;
            _time = time;
            _behaviours = new PredictionCollection(_time);
            _simulation = simulation;
            _clientPlayer = clientPlayer;
            _clientDeltaSnapshot = clientDeltaSnapshot;
            this.clientTickRunner = clientTickRunner;

            this.world = world;
            world.onSpawn += OnSpawn;
            world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (var item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }
        }

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

        public void OnTickSkip()
        {
            if (logger.LogEnabled()) logger.Log($"Tick Skip");

            // clear inputs, start a fresh
            // set to no value so SendInput can handle it as if there are no acks
            ackedInput = null;
        }

        private void OnSpawn(NetworkIdentity identity)
        {
            _behaviours.Add(identity, out var _, out var foundBehaviours);

            foreach (var behaviour in foundBehaviours)
            {
                if (logger.LogEnabled()) logger.Log($"Spawned (netId:{((NetworkBehaviour)behaviour).NetId},comp:{((NetworkBehaviour)behaviour).ComponentIndex}) {behaviour.GetType()}");

                behaviour.ClientSetup(_bufferSize, _clientDeltaSnapshot.ClientInterpolation);
            }
        }

        private void OnUnspawn(NetworkIdentity identity)
        {
            _behaviours.Remove(identity, out var _, out var _);
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

                // todo do we need different state values for different objects
                //      for physics and owned object they need to be tick N to resimular
                //      but for remote object (objects that are controled by others, and only get interpolated),
                //      they might need to be moved to interpolated value instead of tick N value, does they mean they will be N-2 ticks behind for simulation?
                // then apply last received, and debug create after image
                _clientDeltaSnapshot.WorldSnapshot.SetActivePtr(from - 1);

                for (var i = 0; i < count; i++)
                {
                    var behaviour = behaviours[i];
                    behaviour.AfterStateChanged();

                    if (behaviour is IDebugPredictionAfterImage debug && debug.ShowAfterImage)
                    {
                        var snapshotBehavioir = (ISnapshotBehaviourGenerated)behaviour;
                        // just re-use this struct for debug, it just needs the current snapsot in order to display effect
                        var snapshots = new ResimulationSnapshot()
                        {
                            Before = snapshotBehavioir.Ptr,
                            NameToOffset = snapshotBehavioir.SnapshotMetadata.NameToOffset
                        };
                        debug.CreateAfterImage(snapshots, new Color(1f, 0.4f, 0f));
                    }
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

                _clientDeltaSnapshot.WorldSnapshot.CopyFromPreviousTick(tick);

                var updates = _behaviours.GetUpdates();
                for (var i = 0; i < updates.Count; i++)
                {
                    try
                    {
                        var update = updates[i];
                        // if behaviour run full tick stuff, otherwise just call fixedupdate
                        if (update is IPredictionBehaviour behaviour)
                            behaviour.ClientController.Simulate(tick);
                        else
                            update.NetworkFixedUpdate();
                    }
                    catch (Exception e)
                    {
                        logger.LogException(e);
                    }
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

            if (NeedResimulation(out var lastReceived))
            {
                _lastResimulation = lastReceived;

                // from +1 because we receive N, so we need to simulate n+1
                // sim up to N-1, we do N below when we get new inputs
                Resimulate(lastReceived + 1, tick - 1);
            }

            _time.Tick = tick;

            var behaviours = _behaviours.GetBehaviours();
            var count = behaviours.Count;

            if (lastInputTick != 0 && lastInputTick != tick - 1)
                if (logger.WarnEnabled()) logger.LogWarning($"Inputs ticks called out of order. Last:{lastInputTick} tick:{tick}");
            lastInputTick = tick;

            for (var i = 0; i < count; i++)
            {
                var behaviour = behaviours[i];
                // get and send inputs
                if (behaviour.UseInputs())
                    behaviour.ClientController.InputTick(tick);
            }

            Simulate(tick);
            _time.Method = UpdateMethod.None;
        }

        public void AfterAllTicks()
        {
            // AfterAllTicks is called even if there have been no ticks this frame, so we need to make sure we dont send state we have already sent
            if (_lastSend == lastInputTick)
                return;

            // only send input for last tick of the frame
            // - we send previous inputs with current
            // - sending ticks so close together will have a high chance of arriving out of order, so older state will be dropped aynway
            SendInputs(lastInputTick);
            _lastSend = lastInputTick;
        }

        private void SendInputs(int tick)
        {
            // no value means this is first send
            // for this case we can just send the acked value to tick-1 so that only new input is sent
            // next frame it will send this and next frames inputs like it should normally
            if (ackedInput == null)
            {
                if (logger.LogEnabled()) logger.Log($"no acked inputs, setting acked to {tick - 1}");
                ackedInput = tick - 1;
            }

            if (ReadyForWorldState)
                SendReadyInputs(tick);
            else
                // todo should we send this every frame if there has been no new ticks
                //      if we do this, make sure to also change how ReadyForWorldState is set on server
                SendNotReadyInputs();
        }

        private void SendReadyInputs(int tick)
        {
            if (tick == ackedInput.Value)
            {
                if (logger.LogEnabled()) logger.Log($"No new inputs for tick {tick}. This is probaly because no fixedupdate ran this frame");
                return;
            }

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
                    Tick = tick,
                    ClientTime = _time.UnscaledTime,
                    NumberOfInputs = length,
                    Payload = writer.ToArraySegment(),
                };

                var token = TickNotifyToken.GetToken(this, tick);
                _clientPlayer.Send(message, token);
            }
        }

        private void SendNotReadyInputs()
        {
            var message = new InputStateNotReady
            {
                ClientTime = _time.UnscaledTime,
            };

            _clientPlayer.Send(message, Channel.Unreliable);
        }
    }
}
