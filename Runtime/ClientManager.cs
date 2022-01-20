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

        readonly Dictionary<uint, IPredictionBehaviour> behaviours = new Dictionary<uint, IPredictionBehaviour>();

        readonly bool hostMode;
        readonly IPredictionSimulation simulation;
        readonly IPredictionTime time;
        readonly ClientTickRunner clientTickRunner;
        readonly ClientTime clientTime;

        int lastReceivedTick = Helper.NO_VALUE;
        bool unappliedTick;

        public ClientManager(bool hostMode, IPredictionSimulation simulation, TickRunner tickRunner, NetworkWorld world, MessageHandler messageHandler)
        {
            throw new NotSupportedException();
            //if (!hostMode)
            //    throw new InvalidOperationException("Wrong constructor used for non-host mode");

            //this.hostMode = hostMode;
            //this.simulation = simulation;
            //time = tickRunner;
            //tickRunner.onTick += Tick;

            //messageHandler.RegisterHandler<WorldState>(ReceiveWorldState);
            //world.onSpawn += OnSpawn;
            //world.onUnspawn += OnUnspawn;

            //// add existing items
            //foreach (NetworkIdentity item in world.SpawnedIdentities)
            //{
            //    OnSpawn(item);
            //}
        }
        public ClientManager(IPredictionSimulation simulation, ClientTickRunner clientTickRunner, NetworkWorld world, MessageHandler messageHandler)
        {
            hostMode = false;
            this.simulation = simulation;
            time = clientTickRunner;
            this.clientTickRunner = clientTickRunner;
            this.clientTickRunner.onTick += Tick;
            this.clientTickRunner.OnTickSkip += OnTickSkip;
            clientTime = new ClientTime(time.FixedDeltaTime);

            messageHandler.RegisterHandler<WorldState>(ReceiveWorldState);
            world.onSpawn += OnSpawn;
            world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (NetworkIdentity item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }
        }

        void ThrowIfHostMode()
        {
            if (hostMode)
                throw new InvalidOperationException("Should not be called in host mode");
        }

        private void OnTickSkip()
        {
            ThrowIfHostMode();

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.OnTickSkip();
        }

        public void OnSpawn(NetworkIdentity identity)
        {
            if (identity.TryGetComponent(out IPredictionBehaviour behaviour))
            {
                if (logger.LogEnabled()) logger.Log($"Spawned {identity.NetId} {behaviour.GetType()}");
                behaviours.Add(identity.NetId, behaviour);
                behaviour.ClientSetup(clientTime);
            }
        }
        public void OnUnspawn(NetworkIdentity identity)
        {
            behaviours.Remove(identity.NetId);
        }

        void ReceiveWorldState(INetworkPlayer _, WorldState state)
        {
            // do nothing with state in hostmode
            // because server and client objects are the same
            if (hostMode)
                return;

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
                while (reader.CanReadBytes(1))
                {
                    uint netId = reader.ReadPackedUInt32();
                    if (!behaviours.ContainsKey(netId))
                    {
                        // todo fix spawning 
                        // this breaks if state message is received before Mirage's spawn messages
                        logger.LogWarning($"(TODO FIX THIS) No key for {netId}, Stoping ReceiveState");
                        return;
                    }

                    IPredictionBehaviour behaviour = behaviours[netId];
                    behaviour.ClientController.ReceiveState(tick, reader);
                }
            }
        }

        void Resimulate(int from, int to)
        {
            ThrowIfHostMode();

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
            ThrowIfHostMode();

            clientTime.Tick = tick;

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.Simulate(tick);
            simulation.Simulate(time.FixedDeltaTime);
        }

        internal void Tick(int tick)
        {
            // do nothing in host mode, server controller is in charge
            if (hostMode) return;

            // set lastSim to +1, so if we receive new snapshot, then we sim up to 106 again
            // we only want to step forward 1 tick at a time so we collect inputs, and sim correctly
            // todo: what happens if we do 2 at once, is that really a problem?

            if (unappliedTick)
            {
                // sim up to N-1, we do N below when we get new inputs
                Resimulate(lastReceivedTick, tick - 1);
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

        private int lastInputTick;
        Dictionary<int, TInput> pendingInputs = new Dictionary<int, TInput>();
        private TState beforeResimulateState;

        public ClientController(PredictionBehaviourBase<TInput, TState> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;
            if (behaviour.UseInputs())
                _inputBuffer = new TInput[bufferSize];
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

            if (behaviour is IDebugPredictionBehaviour debug)
                debug.CreateAfterImage(lastReceivedState);
        }
        public void AfterResimulate()
        {
            ThrowIfHostMode();

            TState next = behaviour.GatherState();
            behaviour.ResimulationTransition(beforeResimulateState, next);
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
            SetInput(tick, thisTickInput);
            SendInput(tick, thisTickInput);
        }

        void SendInput(int tick, TInput input)
        {
            if (behaviour.IsLocalClient)
            {
                behaviour.ServerController.ReceiveHostInput(tick, input);
                return;
            }

            if (behaviour is IDebugPredictionBehaviour debug)
                debug.Copy?.NoNetworkApply(input);

            pendingInputs.Add(tick, input);

            var inputs = new TInput[pendingInputs.Count];
            foreach (KeyValuePair<int, TInput> pending in pendingInputs)
            {
                inputs[tick - pending.Key] = pending.Value;
            }

            IConnection conn = behaviour.Client.Player.Connection;

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                behaviour.PackInputMessage(writer, tick, inputs);

                if (logger.LogEnabled()) logger.Log($"sending inputs for {tick}. length: {inputs.Length}");
                INotifyToken token = conn.SendNotify(writer.ToArraySegment());
                token.Delivered += () =>
                {
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        // once message is acked, remove all inputs starting at the tick
                        pendingInputs.Remove(tick - i);
                    }
                };
            }
        }

        public void OnTickSkip()
        {
            // clear inputs, start a fresh
            pendingInputs.Clear();
        }
    }
}
