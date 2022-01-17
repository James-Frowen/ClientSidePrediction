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
using UnityEngine;


namespace JamesFrowen.CSP
{
    /// <summary>
    /// Controls all objects on server
    /// </summary>
    internal class ServerManager
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerManager");

        readonly Dictionary<uint, IPredictionBehaviour> behaviours = new Dictionary<uint, IPredictionBehaviour>();
        readonly IEnumerable<INetworkPlayer> players;
        readonly IPredictionSimulation simulation;
        readonly IPredictionTime time;

        public ServerManager(IEnumerable<INetworkPlayer> players, IPredictionSimulation simulation, IPredictionTime time, NetworkWorld world)
        {
            this.players = players;
            this.simulation = simulation;
            this.time = time;

            world.onSpawn += OnSpawn;
            world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (NetworkIdentity item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }
        }

        private void OnSpawn(NetworkIdentity identity)
        {
            if (identity.TryGetComponent(out IPredictionBehaviour behaviour))
            {
                behaviours.Add(identity.NetId, behaviour);
                behaviour.ServerSetup(time);
            }
        }
        private void OnUnspawn(NetworkIdentity identity)
        {
            behaviours.Remove(identity.NetId);
        }

        public void Tick(int tick)
        {
            if (logger.LogEnabled()) logger.Log($"Server tick {tick}");
            foreach (IPredictionBehaviour behaviour in behaviours.Values)
            {
                behaviour.ServerController.Tick(tick);
            }

            simulation.Simulate(time.FixedDeltaTime);

            var msg = new WorldState() { tick = tick };
            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                foreach (KeyValuePair<uint, IPredictionBehaviour> kvp in behaviours)
                {
                    writer.WritePackedUInt32(kvp.Key);
                    IPredictionBehaviour behaviour = kvp.Value;
                    behaviour.ServerController.WriteState(writer);
                }

                msg.state = writer.ToArraySegment();
                NetworkServer.SendToMany(players, msg, Channel.Unreliable);
            }
        }
    }

    /// <summary>
    /// Controls 1 object on server
    /// </summary>
    /// <typeparam name="TInput"></typeparam>
    /// <typeparam name="TState"></typeparam>
    internal class ServerController<TInput, TState> : IServerController where TInput : IInputState
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerController");

        readonly PredictionBehaviourBase<TInput, TState> behaviour;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;
        /// <summary>Clears Previous inputs for tick (eg tick =100 clears tick 99's inputs</summary>
        void ClearPreviousInput(int tick) => SetInput(tick - 1, default);

        int lastReceived = NEVER_RECEIVED;
        (int tick, TInput input) lastValidInput;
        const int NEVER_RECEIVED = -1;

        int lastSim;

        public ServerController(PredictionBehaviourBase<TInput, TState> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;
            if (behaviour.HasInput)
                _inputBuffer = new TInput[bufferSize];
        }

        void IServerController.WriteState(NetworkWriter writer)
        {
            TState state = behaviour.GatherState();
            writer.Write(state);
        }

        public void OnReceiveInput(INetworkPlayer player, int tick, TInput[] newInputs)
        {
            if (player != behaviour.Owner)
                throw new InvalidOperationException($"player {player} does not have authority to set inputs for object");

            // if lastTick is before last sim, then it is late and we can't use
            if (tick < lastSim)
            {
                // log at start, but warn after
                if (lastReceived == NEVER_RECEIVED)
                {
                    if (logger.LogEnabled()) logger.Log($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSim}. late by {lastSim - tick}. But was at start, so not a problem");
                }
                else
                {
                    if (logger.LogEnabled()) logger.Log($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSim}. late by {lastSim - tick}");
                }
                return;
            }
            else
            {
                if (logger.LogEnabled()) logger.Log($"received inputs for {tick}. length: {newInputs.Length}, lastSim:{lastSim}. early by { tick - lastSim}");
            }

            for (int i = 0; i < newInputs.Length; i++)
            {
                int t = tick - i;
                TInput input = newInputs[i];
                // if new, and after last sim
                if (t > lastReceived && t > lastSim)
                {
                    Debug.Assert(input.Valid);
                    SetInput(t, input);
                }
            }

            lastReceived = Mathf.Max(lastReceived, tick);
        }

        void IServerController.Tick(int tick)
        {
            TInput input = default, previous = default;
            if (behaviour.UseInputs())
            {
                getValidInputs(tick, out input, out previous);
                behaviour.ApplyInputs(input, previous);
            }

            behaviour.NetworkFixedUpdate();

            ClearPreviousInput(tick);
            lastSim = tick;
        }

        void getValidInputs(int tick, out TInput input, out TInput previous)
        {
            input = default;
            previous = default;
            // dont need to do anything till first is received
            if (lastReceived == NEVER_RECEIVED)
                return;

            input = getValidInput(tick);
            previous = getValidInput(tick - 1);
            if (input.Valid)
            {
                lastValidInput = (tick, input);
            }
        }

        TInput getValidInput(int tick)
        {
            TInput input = GetInput(tick);
            if (input.Valid)
            {
                return input;
            }
            else
            {
                if (logger.WarnEnabled()) logger.LogWarning($"No inputs for {tick}");
                return behaviour.MissingInput(lastValidInput.input, lastValidInput.tick, tick);
            }
        }
    }
}
