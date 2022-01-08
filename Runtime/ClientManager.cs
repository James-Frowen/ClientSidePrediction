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
    /// <summary>
    /// Controls all objects on client
    /// </summary>
    internal class ClientManager
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager");
        private readonly PhysicsScene physics;
        private readonly IPredictionTime time;
        readonly NetworkTime networkTime;

        private readonly Dictionary<uint, IPredictionBehaviour> behaviours = new Dictionary<uint, IPredictionBehaviour>();

        int lastReceivedTick = Helper.NO_VALUE;
        bool unappliedTick;
        int lastSimTick;

        public int ClientDelay = 2;

        public ClientManager(PhysicsScene physics, IPredictionTime time, NetworkWorld world, MessageHandler messageHandler)
        {
            this.physics = physics;
            this.time = time;
            networkTime = world.Time;

            messageHandler.RegisterHandler<WorldState>(RecieveWorldState);
            world.onSpawn += OnSpawn;
            world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (NetworkIdentity item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }
        }
        public void OnSpawn(NetworkIdentity identity)
        {
            if (identity.TryGetComponent(out IPredictionBehaviour behaviour))
            {
                behaviours.Add(identity.NetId, behaviour);
                behaviour.ClientSetup(time);
            }
        }
        public void OnUnspawn(NetworkIdentity identity)
        {
            behaviours.Remove(identity.NetId);
        }

        void RecieveWorldState(INetworkPlayer _, WorldState state)
        {
            ReceiveState(state.tick, state.state);
        }
        void ReceiveState(int tick, ArraySegment<byte> statePayload)
        {
            if (lastReceivedTick > tick)
            {
                logger.LogWarning("State out of order");
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
                    IPredictionBehaviour behaviour = behaviours[netId];
                    behaviour.ClientController.ReceiveState(tick, reader);
                }
            }
        }

        public void Resimulate(int receivedTick)
        {
            logger.Log($"Resimulate from {receivedTick} to {lastSimTick}");

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.BeforeResimulate();

            for (int tick = receivedTick; tick < lastSimTick; tick++)
            {
                // set forward Applying inputs
                // - exclude current tick, we will run this later
                if (tick - lastReceivedTick > Helper.BufferSize)
                    throw new OverflowException("Inputs overflowed buffer");

                foreach (IPredictionBehaviour behaviour in behaviours.Values)
                    behaviour.ClientController.Simulate(tick);
                physics.Simulate(time.FixedDeltaTime);
            }

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.AfterResimulate();
        }

        // todo add this tick delay stuff to tick runner rather than inside tick
        // maybe update tick/inputs at same interval as server
        // use tick rate stuff from snapshot interpolation

        // then use real time to send inputs to server??

        // maybe look at https://github.com/Unity-Technologies/FPSSample/blob/master/Assets/Scripts/Game/Main/ClientGameLoop.cs
        internal void Tick(int tick)
        {
            // delay from latency to make sure inputs reach server in time
            float tickDelay = getClientTick();

            int clientTick = tick + (int)Math.Floor(tickDelay);
            while (clientTick > lastSimTick)
            {
                // set lastSim to +1, so if we receive new snapshot, then we sim up to 106 again
                // we only want to step forward 1 tick at a time so we collect inputs, and sim correctly
                // todo: what happens if we do 2 at once, is that really a problem?
                lastSimTick++;

                if (logger.LogEnabled()) logger.Log($"Client tick {lastSimTick}, Client Delay {tickDelay:0.0}");

                if (unappliedTick)
                {
                    Resimulate(lastReceivedTick);
                    unappliedTick = false;
                }

                foreach (IPredictionBehaviour behaviour in behaviours.Values)
                {
                    // get and send inputs
                    behaviour.ClientController.InputTick(lastSimTick);
                    //apply 
                    behaviour.ClientController.Simulate(lastSimTick);
                }
                physics.Simulate(time.FixedDeltaTime);
            }
        }
        private float getClientTick()
        {
            // +2 to make sure inputs always get to server before simulation
            return ClientDelay + calculateTickDifference();
        }
        private float calculateTickDifference()
        {
            double oneWayTrip = networkTime.Rtt / 2;
            float tickTime = time.FixedDeltaTime;

            double tickDifference = oneWayTrip / tickTime;
            return (float)tickDifference;
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

        readonly PredictionBehaviour<TInput, TState> behaviour;
        readonly IPredictionTime time;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;

        int lastReceivedTick = Helper.NO_VALUE;
        TState lastReceivedState;

        int lastSimTick;

        private int lastInputTick;
        Dictionary<int, TInput> pendingInputs = new Dictionary<int, TInput>();
        private TState beforeResimulateState;

        void IClientController.ReceiveState(int tick, NetworkReader reader)
        {
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

        public ClientController(PredictionBehaviour<TInput, TState> behaviour, IPredictionTime time, int bufferSize)
        {
            this.behaviour = behaviour;
            _inputBuffer = new TInput[bufferSize];
            this.time = time;
        }

        void IClientController.BeforeResimulate()
        {
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
        void IClientController.AfterResimulate()
        {
            TState next = behaviour.GatherState();
            behaviour.ApplyStateLerp(beforeResimulateState, next);
            beforeResimulateState = default;
        }

        /// <summary>
        /// From tick N to N+1
        /// </summary>
        /// <param name="tick"></param>
        void IClientController.Simulate(int tick) => Simulate(tick);
        private void Simulate(int tick)
        {
            TInput input = GetInput(tick);
            TInput previous = GetInput(tick - 1);
            behaviour.ApplyInput(input, previous);
            behaviour.NetworkFixedUpdate(time.FixedDeltaTime);
        }

        void IClientController.InputTick(int tick)
        {
            if (!behaviour.HasInput)
                return;

            if (lastInputTick != tick - 1)
                throw new Exception("Inputs ticks called out of order");
            lastInputTick = tick;

            TInput thisTickInput = behaviour.GetInput();
            SetInput(tick, thisTickInput);
            SendInput(tick, thisTickInput);
        }

        void SendInput(int tick, TInput state)
        {
            if (behaviour is IDebugPredictionBehaviour debug)
                debug.Copy?.NoNetworkApply(state);

            pendingInputs.Add(tick, state);

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
    }
}
