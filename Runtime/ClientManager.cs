/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using System.Collections.Generic;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using Mirage.SocketLayer;
using UnityEngine;
using UnityEngine.Assertions;

namespace JamesFrowen.CSP
{
    class ClientTime : IPredictionTime
    {
        readonly IPredictionTime _tickRunner;

        public ClientTime(IPredictionTime tickRunner)
        {
            _tickRunner = tickRunner;
        }

        public float FixedDeltaTime => _tickRunner.FixedDeltaTime;
        public double UnscaledTime => _tickRunner.UnscaledTime;
        public float FixedTime => Tick * FixedDeltaTime;

        public int Tick { get; set; }
        public bool IsResimulation { get; set; }
    }

    /// <summary>
    /// Controls all objects on client
    /// </summary>
    internal class ClientManager : ITickNotifyTracker
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager");

        readonly Dictionary<NetworkBehaviour, IPredictionBehaviour> behaviours = new Dictionary<NetworkBehaviour, IPredictionBehaviour>();

        readonly IPredictionSimulation simulation;
        readonly IPredictionTime time;
        readonly INetworkPlayer _clientPlayer;
        readonly ClientTickRunner clientTickRunner;
        /// <summary>Time used for physics, includes resimilation time. Driven by <see cref="time"/></summary>
        readonly ClientTime clientTime;
        readonly NetworkWorld world;

        int lastReceivedTick = Helper.NO_VALUE;
        bool unappliedTick;
        const int maxInputPerPacket = 8;
        int ackedInput = Helper.NO_VALUE;

        int ITickNotifyTracker.LastAckedTick { get => ackedInput; set => ackedInput = value; }

        public int Debug_ServerTick => lastReceivedTick;

        public ClientManager(IPredictionSimulation simulation, ClientTickRunner clientTickRunner, NetworkWorld world, INetworkPlayer clientPlayer, MessageHandler messageHandler)
        {
            this.simulation = simulation;
            time = clientTickRunner;
            _clientPlayer = clientPlayer;
            this.clientTickRunner = clientTickRunner;
            this.clientTickRunner.onTick += Tick;
            this.clientTickRunner.OnTickSkip += OnTickSkip;
            clientTime = new ClientTime(time);

            messageHandler.RegisterHandler<WorldState>(ReceiveWorldState);
            this.world = world;
            world.onSpawn += OnSpawn;
            world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (NetworkIdentity item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }
        }

        private void OnTickSkip()
        {
            if (logger.LogEnabled()) logger.Log($"Tick Skip");

            // clear inputs, start a fresh
            // set to no value so SendInput can handle it as if there are no acks
            ackedInput = Helper.NO_VALUE;
        }

        public void OnSpawn(NetworkIdentity identity)
        {
            foreach (NetworkBehaviour networkBehaviour in identity.NetworkBehaviours)
            {
                if (networkBehaviour is IPredictionBehaviour behaviour)
                {
                    if (logger.LogEnabled()) logger.Log($"Spawned ({networkBehaviour.NetId},{networkBehaviour.ComponentIndex}) {behaviour.GetType()}");
                    behaviours.Add(networkBehaviour, behaviour);
                    behaviour.ClientSetup(this, clientTime);
                }
            }
        }
        public void OnUnspawn(NetworkIdentity identity)
        {
            foreach (NetworkBehaviour networkBehaviour in identity.NetworkBehaviours)
            {
                if (networkBehaviour is IPredictionBehaviour)
                {
                    behaviours.Remove(networkBehaviour);
                }
            }
        }

        void ReceiveWorldState(INetworkPlayer _, WorldState state)
        {
            ReceiveState(state.tick, state.state);
            clientTickRunner.OnMessage(state.tick, state.ClientTime);
        }
        void ReceiveState(int tick, ArraySegment<byte> statePayload)
        {
            if (lastReceivedTick > tick)
            {
                if (logger.LogEnabled()) logger.Log($"State out of order, Dropping state for {tick}");
                return;
            }

            if (logger.LogEnabled()) logger.Log($"received STATE for {tick}");
            unappliedTick = true;
            lastReceivedTick = tick;
            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(statePayload, world))
            {
                while (reader.CanReadBytes(1))
                {
                    uint netId = reader.ReadPackedUInt32();
                    Debug.Assert(netId != 0);
                    byte componentIndex = reader.ReadByte();

                    if (!world.TryGetIdentity(netId, out NetworkIdentity identity))
                    {
                        // todo fix spawning 
                        // this breaks if state message is received before Mirage's spawn messages
                        logger.LogWarning($"(TODO FIX THIS) Could not find NetworkIdentity with id={netId}, Stoping ReceiveState");
                        return;
                    }

                    NetworkBehaviour networkBehaviour = identity.NetworkBehaviours[componentIndex];


                    Debug.Assert(behaviours.ContainsKey(networkBehaviour));
                    Debug.Assert(networkBehaviour is IPredictionBehaviour);

                    var behaviour = (IPredictionBehaviour)networkBehaviour;
                    // dont use assert, because string alloc
                    if (behaviour.ClientController == null)
                        logger.LogError($"Null ClientController for ({networkBehaviour.NetId},{networkBehaviour.ComponentIndex})");
                    behaviour.ClientController.ReceiveState(reader, tick);
                }
            }
        }

        void Resimulate(int from, int to)
        {
            if (from > to)
            {
                logger.LogError($"Cant resimulate because 'from' was after 'to'. From:{from} To:{to}");
                return;
            }
            if (to - from > Helper.BufferSize)
            {
                logger.LogError($"Cant resimulate more than BufferSize. From:{from} To:{to}");
                return;
            }
            if (logger.LogEnabled()) logger.Log($"Resimulate from {from} to {to}");

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.BeforeResimulate();

            // step forward Applying inputs
            // - include lastSimTick tick, because resim will be called before next tick
            clientTime.IsResimulation = true;
            for (int tick = from; tick <= to; tick++)
            {
                Simulate(tick);
            }
            clientTime.IsResimulation = false;

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.AfterResimulate();
        }

        void Simulate(int tick)
        {
            clientTime.Tick = tick;

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.Simulate(tick);
            simulation.Simulate(time.FixedDeltaTime);
        }

        internal void Tick(int tick)
        {
            // set lastSim to +1, so if we receive new snapshot, then we sim up to 106 again
            // we only want to step forward 1 tick at a time so we collect inputs, and sim correctly
            // todo: what happens if we do 2 at once, is that really a problem?

            if (unappliedTick)
            {
                // from +1 because we receive N, so we need to simulate n+1
                // sim up to N-1, we do N below when we get new inputs
                Resimulate(lastReceivedTick + 1, tick - 1);
                unappliedTick = false;
            }

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
            {
                // get and send inputs
                if (behaviour.UseInputs())
                    behaviour.ClientController.InputTick(tick);
            }

            SendInputs(tick);

            Simulate(tick);
        }

        void SendInputs(int tick)
        {
            // no value means this is first send
            // for this case we can just send the acked value to tick-1 so that only new input is sent
            // next frame it will send this and next frames inputs like it should normally
            if (ackedInput == Helper.NO_VALUE)
                ackedInput = tick - 1;

            if (logger.LogEnabled()) logger.Log($"sending inputs for {tick}. length: {tick - ackedInput}");

            Debug.Assert(tick > ackedInput, "new input should not have been acked before it was sent");

            // write netid
            // write number of inputs
            // write each input

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                int numberOfTicks = tick - ackedInput;
                int length = Math.Min(numberOfTicks, maxInputPerPacket);
                Assert.IsTrue(1 <= length && length <= 8);

                foreach (IPredictionBehaviour behaviour in behaviours.Values)
                {
                    // get and send inputs
                    if (behaviour.UseInputs())
                    {
                        var nb = (NetworkBehaviour)behaviour;
                        Debug.Assert(nb.HasAuthority);
                        writer.WriteNetworkBehaviour(nb);
                        for (int i = 0; i < length; i++)
                        {
                            int t = tick - i;
                            behaviour.ClientController.WriteInput(writer, t);
                        }
                    }
                }

                var message = new InputState
                {
                    tick = tick,
                    clientTime = time.UnscaledTime,
                    length = length,
                    payload = writer.ToArraySegment(),
                };

                INotifyCallBack token = TickNotifyToken.GetToken(this, tick);
                _clientPlayer.Send(message, token);
            }
        }
    }

    /// <summary>
    /// Controls 1 object
    /// </summary>
    /// <typeparam name="TInput"></typeparam>
    /// <typeparam name="TState"></typeparam>
    internal class ClientController<TInput, TState> : IClientController
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientController");

        readonly PredictionBehaviourBase<TInput, TState> behaviour;

        NullableRingBuffer<TInput> _inputBuffer;
        NullableRingBuffer<TState> _stateBuffer;

        int lastReceivedTick = Helper.NO_VALUE;
        TState lastReceivedState;
        TState beforeResimulateState;

        private int lastInputTick;

        public ClientController(PredictionBehaviourBase<TInput, TState> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;

            _stateBuffer = new NullableRingBuffer<TState>(bufferSize, behaviour as ISnapshotDisposer<TState>);
            if (behaviour.UseInputs())
                _inputBuffer = new NullableRingBuffer<TInput>(bufferSize, behaviour as ISnapshotDisposer<TInput>);
        }

        void ThrowIfHostMode()
        {
            if (behaviour.IsLocalClient)
                throw new InvalidOperationException("Should not be called in host mode");
        }

        public void ReceiveState(NetworkReader reader, int tick)
        {
            ThrowIfHostMode();

            TState state = reader.Read<TState>();
            if (lastReceivedTick > tick)
            {
                logger.LogWarning("State out of order");
                return;
            }
            _stateBuffer.Set(tick, state);

            if (logger.LogEnabled()) logger.Log($"received STATE for {tick}");
            lastReceivedTick = tick;
            lastReceivedState = state;
        }

        public void BeforeResimulate()
        {
            ThrowIfHostMode();

            // if receivedTick = 100
            // then we want to Simulate (100->101)
            // so we pass tick 100 into Simulate
            if (behaviour.EnableResimulationTransition)
                beforeResimulateState = behaviour.GatherState();

            // if lastSimTick = 105
            // then our last sim step will be (104->105)
            behaviour.ApplyState(lastReceivedState);

            if (behaviour is IDebugPredictionAfterImage debug)
                debug.CreateAfterImage(lastReceivedState, new Color(1f, 0.4f, 0f));
        }

        public void AfterResimulate()
        {
            ThrowIfHostMode();

            if (behaviour.EnableResimulationTransition)
            {
                TState next = behaviour.GatherState();
                behaviour.ResimulationTransition(beforeResimulateState, next);
                if (behaviour is IDebugPredictionAfterImage debug)
                    debug.CreateAfterImage(next, new Color(0, 0.4f, 1f));

                if (behaviour is ISnapshotDisposer<TState> disposer)
                {
                    disposer.DisposeState(next);
                    disposer.DisposeState(beforeResimulateState);
                }
                beforeResimulateState = default;
            }
        }

        /// <summary>
        /// From tick N to N+1
        /// </summary>
        /// <param name="tick"></param>
        void IClientController.Simulate(int tick)
        {
            ThrowIfHostMode();

            if (behaviour.UseInputs())
            {
                TInput input = _inputBuffer.Get(tick);
                TInput previous = _inputBuffer.Get(tick - 1);
                behaviour.ApplyInputs(input, previous);
            }
            behaviour.NetworkFixedUpdate();
        }

        public void InputTick(int tick)
        {
            Assert.IsTrue(behaviour.UseInputs());

            if (lastInputTick != 0 && lastInputTick != tick - 1)
                if (logger.WarnEnabled()) logger.LogWarning($"Inputs ticks called out of order. Last:{lastInputTick} tick:{tick}");
            lastInputTick = tick;

            TInput thisTickInput = behaviour.GetInput();
            _inputBuffer.Set(tick, thisTickInput);

            if (behaviour is IDebugPredictionLocalCopy debug)
                debug.Copy?.NoNetworkApply(_inputBuffer.Get(tick));
        }

        void IClientController.WriteInput(NetworkWriter writer, int tick)
        {
            TInput input = _inputBuffer.Get(tick);
            writer.Write(input);
        }
    }
}
