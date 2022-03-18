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

        readonly Dictionary<NetworkBehaviour, IPredictionBehaviour> behaviours = new Dictionary<NetworkBehaviour, IPredictionBehaviour>();
        readonly IEnumerable<INetworkPlayer> players;
        readonly IPredictionSimulation simulation;
        readonly IPredictionTime time;

        bool hostMode;
        internal void SetHostMode()
        {
            hostMode = true;
            foreach (KeyValuePair<NetworkBehaviour, IPredictionBehaviour> behaviour in behaviours)
            {
                behaviour.Value.ServerController.SetHostMode();
            }
        }

        public ServerManager(IEnumerable<INetworkPlayer> players, IPredictionSimulation simulation, TickRunner tickRunner, NetworkWorld world)
        {
            this.players = players;
            this.simulation = simulation;
            time = tickRunner;
            tickRunner.onTick += Tick;

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
            if (logger.LogEnabled()) logger.Log($"OnSpawn for {identity.NetId}");
            foreach (NetworkBehaviour networkBehaviour in identity.NetworkBehaviours)
            {
                // todo is using NetworkBehaviour as key ok? or does this need optimizing
                if (networkBehaviour is IPredictionBehaviour behaviour)
                {
                    if (logger.LogEnabled()) logger.Log($"Found PredictionBehaviour for {identity.NetId} {behaviour.GetType().Name}");
                    behaviours.Add(networkBehaviour, behaviour);
                    behaviour.ServerSetup(time);
                    if (hostMode)
                        behaviour.ServerController.SetHostMode();
                }
            }
        }
        private void OnUnspawn(NetworkIdentity identity)
        {
            foreach (NetworkBehaviour networkBehaviour in identity.NetworkBehaviours)
            {
                if (networkBehaviour is IPredictionBehaviour)
                {
                    behaviours.Remove(networkBehaviour);
                }
            }
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
                foreach (KeyValuePair<NetworkBehaviour, IPredictionBehaviour> kvp in behaviours)
                {
                    writer.WriteNetworkBehaviour(kvp.Key);

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
    internal class ServerController<TInput, TState> : IServerController
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerController");

        readonly PredictionBehaviourBase<TInput, TState> behaviour;

        NullableRingBuffer<TInput> _inputBuffer;

        int lastReceived = Helper.NO_VALUE;
        (int tick, TInput input) lastValidInput;
        int lastSim;

        bool hostMode;
        void IServerController.SetHostMode()
        {
            hostMode = true;
        }

        public ServerController(PredictionBehaviourBase<TInput, TState> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;
            if (behaviour.UseInputs())
                _inputBuffer = new NullableRingBuffer<TInput>(bufferSize);
        }

        void IServerController.WriteState(NetworkWriter writer)
        {
            TState state = behaviour.GatherState();
            writer.Write(state);
        }

        public void OnReceiveInput(int tick, ArraySegment<byte> payload)
        {
            if (!ValidateInputTick(tick))
                return;

            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(payload))
            {
                int inputTick = tick;
                while (reader.CanReadBytes(1))
                {
                    TInput input = reader.Read<TInput>();
                    // if new, and after last sim
                    if (inputTick > lastReceived && inputTick > lastSim)
                    {
                        _inputBuffer.Set(inputTick, input);
                    }

                    // inputs written in reverse order, so --
                    inputTick--;
                }
            }

            lastReceived = Mathf.Max(lastReceived, tick);
        }

        private bool ValidateInputTick(int tick)
        {
            // if lastTick is before last sim, then it is late and we can't use
            if (tick >= lastSim)
            {
                if (logger.LogEnabled()) logger.Log($"received inputs for {tick}. lastSim:{lastSim}. early by {tick - lastSim}");
                return true;
            }

            // log at start, but warn after
            if (lastReceived == Helper.NO_VALUE)
            {
                if (logger.LogEnabled()) logger.Log($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSim}. late by {lastSim - tick}. But was at start, so not a problem");
            }
            else
            {
                if (logger.LogEnabled()) logger.Log($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSim}. late by {lastSim - tick}");
            }
            return false;
        }

        void IServerController.Tick(int tick)
        {
            bool hasInputs = behaviour.UseInputs();
            if (hasInputs)
            {
                // hostmode + host client has HasAuthority
                if (hostMode && behaviour.HasAuthority)
                {
                    TInput thisTickInput = behaviour.GetInput();
                    _inputBuffer.Set(tick, thisTickInput);
                }

                getValidInputs(tick, out TInput input, out TInput previous);
                behaviour.ApplyInputs(input, previous);
            }

            behaviour.NetworkFixedUpdate();

            if (hasInputs)
                _inputBuffer.Clear(tick - 1);
            lastSim = tick;
        }

        void getValidInputs(int tick, out TInput input, out TInput previous)
        {
            input = default;
            previous = default;
            // dont need to do anything till first is received
            // skip check hostmode, there are always inputs for hostmode
            if (!hostMode && lastReceived == Helper.NO_VALUE)
                return;

            getValidInput(tick, out bool currentValid, out input);
            getValidInput(tick - 1, out bool _, out previous);
            if (currentValid)
            {
                lastValidInput = (tick, input);
            }
        }

        void getValidInput(int tick, out bool valid, out TInput input)
        {
            valid = _inputBuffer.TryGet(tick, out input);
            if (!valid)
            {
                if (logger.LogEnabled()) logger.Log($"No inputs for {tick}");
                input = behaviour.MissingInput(lastValidInput.input, lastValidInput.tick, tick);
            }
        }

        void IServerController.ReceiveHostInput<TInput2>(int tick, TInput2 _input)
        {
            // todo check Alloc from boxing
            if (_input is TInput input)
            {
                _inputBuffer.Set(tick, input);
            }
            else
            {
                throw new InvalidOperationException("Input type didn't match");
            }
        }
    }
}
