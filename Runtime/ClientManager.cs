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

namespace JamesFrowen.CSP
{
    class ClientTime : IPredictionTime
    {
        public ClientTime(float fixedDeltaTime)
        {
            FixedDeltaTime = fixedDeltaTime;
        }

        public float FixedDeltaTime { get; }
        public int Tick { get; set; }

        public float FixedTime => Tick * FixedDeltaTime;
    }

    /// <summary>
    /// Controls all objects on client
    /// </summary>
    internal class ClientManager
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager");

        readonly Dictionary<NetworkBehaviour, IPredictionBehaviour> behaviours = new Dictionary<NetworkBehaviour, IPredictionBehaviour>();

        readonly IPredictionSimulation simulation;
        readonly IPredictionTime time;
        readonly ClientTickRunner clientTickRunner;
        readonly ClientTime clientTime;
        readonly NetworkWorld world;

        int lastReceivedTick = Helper.NO_VALUE;
        bool unappliedTick;

        public ClientManager(IPredictionSimulation simulation, ClientTickRunner clientTickRunner, NetworkWorld world, MessageHandler messageHandler)
        {
            this.simulation = simulation;
            time = clientTickRunner;
            this.clientTickRunner = clientTickRunner;
            this.clientTickRunner.onTick += Tick;
            this.clientTickRunner.OnTickSkip += OnTickSkip;
            clientTime = new ClientTime(time.FixedDeltaTime);

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
            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.OnTickSkip();
        }

        public void OnSpawn(NetworkIdentity identity)
        {
            foreach (NetworkBehaviour networkBehaviour in identity.NetworkBehaviours)
            {
                if (networkBehaviour is IPredictionBehaviour behaviour)
                {
                    if (logger.LogEnabled()) logger.Log($"Spawned ({networkBehaviour.NetId},{networkBehaviour.ComponentIndex}) {behaviour.GetType()}");
                    behaviours.Add(networkBehaviour, behaviour);
                    behaviour.ClientSetup(clientTime);
                }
            }
        }
        public void OnUnspawn(NetworkIdentity identity)
        {
            foreach (NetworkBehaviour networkBehaviour in identity.NetworkBehaviours)
            {
                if (networkBehaviour is IPredictionBehaviour behaviour)
                {
                    behaviours.Remove(networkBehaviour);
                }
            }
        }

        void ReceiveWorldState(INetworkPlayer _, WorldState state)
        {
            ReceiveState(state.tick, state.state);
            clientTickRunner.OnMessage(state.tick);
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
            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(statePayload))
            {
                reader.ObjectLocator = world;

                while (reader.CanReadBytes(1))
                {
                    NetworkBehaviour networkBehaviour = reader.ReadNetworkBehaviour();
                    if (networkBehaviour == null)
                    {
                        // todo fix spawning 
                        // this breaks if state message is received before Mirage's spawn messages
                        logger.LogWarning($"(TODO FIX THIS) had null networkbehaviour, Stoping ReceiveState");
                        return;
                    }

                    Debug.Assert(behaviours.ContainsKey(networkBehaviour));
                    Debug.Assert(networkBehaviour is IPredictionBehaviour);

                    var behaviour = (IPredictionBehaviour)networkBehaviour;
                    Debug.Assert(behaviour.ClientController != null, $"Null ClientController for ({networkBehaviour.NetId},{networkBehaviour.ComponentIndex})");
                    behaviour.ClientController.ReceiveState(tick, reader);
                }
            }
        }

        void Resimulate(int from, int to)
        {
            logger.Log($"Resimulate from {from} to {to}");

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.BeforeResimulate();

            // step forward Applying inputs
            // - include lastSimTick tick, because resim will be called before next tick
            for (int tick = from; tick <= to; tick++)
            {
                if (tick - from > Helper.BufferSize)
                    throw new OverflowException("Inputs overflowed buffer");

                Simulate(tick);
            }

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
                behaviour.ClientController.InputTick(tick);
            }


            Simulate(tick);
        }
    }

    /// <summary>
    /// Controls 1 object
    /// </summary>
    /// <typeparam name="TInput"></typeparam>
    /// <typeparam name="TState"></typeparam>
    internal class ClientController<TInput, TState> : IClientController where TInput : IInputState
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientController");

        readonly PredictionBehaviourBase<TInput, TState> behaviour;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;

        int lastReceivedTick = Helper.NO_VALUE;
        TState lastReceivedState;
        TState beforeResimulateState;

        private int lastInputTick;

        const int maxInputPerPacket = 8;
        private int ackedInput = Helper.NO_VALUE;

        public ClientController(PredictionBehaviourBase<TInput, TState> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;
            if (behaviour.UseInputs())
            {
                _inputBuffer = new TInput[bufferSize];
            }
        }

        void ThrowIfHostMode()
        {
            if (behaviour.IsLocalClient)
                throw new InvalidOperationException("Should not be called in host mode");
        }

        public void ReceiveState(int tick, NetworkReader reader)
        {
            ThrowIfHostMode();

            TState state = reader.Read<TState>();
            if (lastReceivedTick > tick)
            {
                logger.LogWarning("State out of order");
                return;
            }

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

            TState next = behaviour.GatherState();
            behaviour.ResimulationTransition(beforeResimulateState, next);
            if (behaviour is IDebugPredictionAfterImage debug)
                debug.CreateAfterImage(next, new Color(0, 0.4f, 1f));
            beforeResimulateState = default;
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
                TInput input = GetInput(tick);
                TInput previous = GetInput(tick - 1);
                behaviour.ApplyInputs(input, previous);
            }
            behaviour.NetworkFixedUpdate();
        }

        public void InputTick(int tick)
        {
            if (!behaviour.UseInputs())
                return;

            if (lastInputTick != 0 && lastInputTick != tick - 1)
                if (logger.WarnEnabled()) logger.LogWarning($"Inputs ticks called out of order. Last:{lastInputTick} tick:{tick}");
            lastInputTick = tick;

            TInput thisTickInput = behaviour.GetInput();
            thisTickInput.Valid = true;
            SetInput(tick, thisTickInput);
            SendInput(tick);
        }

        public void OnTickSkip()
        {
            // clear inputs, start a fresh
            // set to no value so SendInput can handle it as if there are no acks
            ackedInput = Helper.NO_VALUE;
        }

        void SendInput(int tick)
        {
            if (behaviour.IsLocalClient)
            {
                behaviour.ServerController.ReceiveHostInput(tick, GetInput(tick));
                return;
            }

            if (behaviour is IDebugPredictionLocalCopy debug)
                debug.Copy?.NoNetworkApply(GetInput(tick));

            // no value means this is first send
            // for this case we can just send the acked value to tick-1 so that only new input is sent
            // next frame it will send this and next frames inputs like it should normally
            if (ackedInput == Helper.NO_VALUE)
                ackedInput = tick - 1;

            if (logger.LogEnabled()) logger.Log($"sending inputs for {tick}. length: {tick - ackedInput}");

            Debug.Assert(tick > ackedInput, "new input should not have been acked before it was sent");

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                int length = 0;
                // tick -> ackedInput+1
                // must be `t` greater than ack, AND less than max
                for (int t = tick; t > ackedInput && length < maxInputPerPacket; t--, length++)
                {
                    TInput input = GetInput(t);

                    Debug.Assert(input.Valid, $"Client Sending invalid input, tick:{t}");
                    writer.Write(input);
                }

                var message = new InputMessage
                {
                    behaviour = behaviour,
                    tick = tick,
                    payload = writer.ToArraySegment(),
                };

                INotifyCallBack token = NotifyToken.GetToken(this, tick);

                INetworkClient client = behaviour.Client;
                client.Send(message, token);
            }
        }

        class NotifyToken : INotifyCallBack
        {
            static Pool<NotifyToken> pool = new Pool<NotifyToken>((_, __) => new NotifyToken(), 0, 10, Helper.BufferSize, logger);
            public static INotifyCallBack GetToken(ClientController<TInput, TState> controller, int tick)
            {
                NotifyToken token = pool.Take();
                token.controller = controller;
                token.tick = tick;
                return token;
            }

            ClientController<TInput, TState> controller;
            int tick;

            public void OnDelivered()
            {
                // take highest value of current ack and new ack
                controller.ackedInput = Math.Max(controller.ackedInput, tick);
                pool.Put(this);
            }

            public void OnLost()
            {
                pool.Put(this);
            }
        }
    }
}
