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
using System.Linq;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using Mirage.SocketLayer;
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
        // keep track of player list ourselves, so that we can use the SendToMany that does not allocate
        readonly List<INetworkPlayer> _players;
        readonly Dictionary<INetworkPlayer, PlayerTimeTracker> _playerTracker = new Dictionary<INetworkPlayer, PlayerTimeTracker>();
        readonly IPredictionSimulation simulation;
        readonly IPredictionTime time;
        readonly NetworkWorld _world;

        bool hostMode;

        internal int lastSim;


        public PlayerTimeTracker Debug_FirstPlayertracker => _playerTracker.Values.FirstOrDefault();

        internal void SetHostMode()
        {
            hostMode = true;
            foreach (KeyValuePair<NetworkBehaviour, IPredictionBehaviour> behaviour in behaviours)
            {
                behaviour.Value.ServerController.SetHostMode();
            }
        }

        public ServerManager(IPredictionSimulation simulation, TickRunner tickRunner, NetworkWorld world)
        {
            _players = new List<INetworkPlayer>();
            this.simulation = simulation;
            time = tickRunner;
            tickRunner.onTick += Tick;

            _world = world;
            _world.onSpawn += OnSpawn;
            _world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (NetworkIdentity item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }
        }

        public void AddPlayer(INetworkPlayer player)
        {
            _players.Add(player);
            _playerTracker.Add(player, new PlayerTimeTracker());
        }
        public void RemovePlayer(INetworkPlayer player)
        {
            _players.Remove(player);
            _playerTracker.Remove(player);
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
                    behaviour.ServerSetup(this, time);
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
            simulate(tick);
            SendState(tick);
        }

        private void simulate(int tick)
        {
            foreach (IPredictionBehaviour behaviour in behaviours.Values)
            {
                behaviour.ServerController.Tick(tick);
            }
            simulation.Simulate(time.FixedDeltaTime);
            lastSim = tick;
        }

        private void SendState(int tick)
        {
            // todo get max size from config
            const int MAX_SIZE = 1157; // max notify size
            var msg = new WorldState() { tick = tick };
            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                int size = 0;
                foreach (KeyValuePair<NetworkBehaviour, IPredictionBehaviour> kvp in behaviours)
                {
                    writer.WriteNetworkBehaviour(kvp.Key);

                    IPredictionBehaviour behaviour = kvp.Value;
                    behaviour.ServerController.WriteState(writer, tick);

                    if (logger.LogEnabled())
                    {
                        int newSize = writer.ByteLength;
                        int behaviourSize = newSize - size;
                        size = newSize;
                        logger.Log($"Writing Behaviour:{kvp.Key.name} NetId:{kvp.Key.NetId} size:{behaviourSize}");
                    }

                    if (writer.ByteLength > MAX_SIZE)
                    {
                        logger.LogError($"Can't send state over max size of {MAX_SIZE}.");
                        return;
                    }
                }

                var payload = writer.ToArraySegment();
                for (int i = 0; i < _players.Count; i++)
                {
                    INetworkPlayer player = _players[i];
                    PlayerTimeTracker tracker = _playerTracker[player];

                    // dont send if not ready
                    msg.state = tracker.ReadyForWorldState ? payload : default;

                    // set client time for each client, 
                    // todo find way to avoid serialize multiple times
                    msg.ClientTime = tracker.LastReceivedClientTime;

                    INotifyCallBack token = TickNotifyToken.GetToken(tracker, tick);
                    player.Send(msg, token);
                }
            }
        }

        internal void HandleInput(INetworkPlayer player, InputState message)
        {
            PlayerTimeTracker tracker = _playerTracker[player];
            tracker.LastReceivedClientTime = Math.Max(tracker.LastReceivedClientTime, message.clientTime);
            // check if inputs have arrived in time and in order, otherwise we can't do anything with them.
            if (!ValidateInputTick(tracker, message.tick))
                return;

            tracker.ReadyForWorldState = message.ready;

            int length = message.length;
            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(message.payload, _world))
            {
                // keep reading while there is atleast 1 byte
                // netBehaviour will be alteast 1 byte
                while (reader.CanReadBytes(1))
                {
                    NetworkBehaviour networkBehaviour = reader.ReadNetworkBehaviour();

                    if (networkBehaviour == null)
                    {
                        if (logger.WarnEnabled()) logger.LogWarning($"Spawned object not found when handling InputMessage message");
                        return;
                    }

                    if (player != networkBehaviour.Owner)
                        throw new InvalidOperationException($"player {player} does not have authority to set inputs for object. Object[Netid:{networkBehaviour.NetId}, name:{networkBehaviour.name}]");

                    if (!(networkBehaviour is IPredictionBehaviour behaviour))
                        throw new InvalidOperationException($"Networkbehaviour({networkBehaviour.NetId}, {networkBehaviour.ComponentIndex}) was not a IPredictionBehaviour");

                    int inputTick = message.tick;
                    for (int i = 0; i < length; i++)
                    {
                        int t = inputTick - i;
                        behaviour.ServerController.ReadInput(tracker, reader, t);
                    }
                }
            }

            tracker.lastReceivedInput = Mathf.Max(tracker.lastReceivedInput, message.tick);
        }

        private bool ValidateInputTick(PlayerTimeTracker tracker, int tick)
        {
            // received inputs out of order
            // we can ignore them, input[n+1] will contain input[n], so we would have no new inputs in this packet
            if (tracker.lastReceivedInput > tick)
            {
                if (logger.LogEnabled()) logger.Log($"received inputs out of order, lastReceived:{tracker.lastReceivedInput} new inputs:{tick}");
                return false;
            }

            // if lastTick is before last sim, then it is late and we can't use
            if (tick >= lastSim)
            {
                if (logger.LogEnabled()) logger.Log($"received inputs for {tick}. lastSim:{lastSim}. early by {tick - lastSim}");
                return true;
            }

            if (tracker.lastReceivedInput == Helper.NO_VALUE)
            {
                if (logger.LogEnabled()) logger.Log($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSim}. late by {lastSim - tick}. But was at start, so not a problem");
            }
            else
            {
                if (logger.LogEnabled()) logger.Log($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSim}. late by {lastSim - tick}");
            }
            return false;
        }

        internal class PlayerTimeTracker : ITickNotifyTracker
        {
            // todo use this to collect metrics about client (eg ping, rtt, etc)
            public double LastReceivedClientTime;
            public int lastReceivedInput = Helper.NO_VALUE;

            public int LastAckedTick { get; set; }

            public bool ReadyForWorldState;
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
        readonly ServerManager _manager;
        NullableRingBuffer<TInput> _inputBuffer;
        NullableRingBuffer<TState> _stateBuffer;

        (int tick, TInput input) lastValidInput;

        int lastReceived = Helper.NO_VALUE;

        bool hostMode;
        void IServerController.SetHostMode()
        {
            hostMode = true;
        }

        public ServerController(ServerManager manager, PredictionBehaviourBase<TInput, TState> behaviour, int bufferSize)
        {
            _manager = manager;
            this.behaviour = behaviour;

            _stateBuffer = new NullableRingBuffer<TState>(bufferSize, behaviour as ISnapshotDisposer<TState>);
            if (behaviour.UseInputs())
                _inputBuffer = new NullableRingBuffer<TInput>(bufferSize, behaviour as ISnapshotDisposer<TInput>);
        }

        void IServerController.WriteState(NetworkWriter writer, int tick)
        {
            TState state = behaviour.GatherState();
            _stateBuffer.Set(tick, state);
            int startBit = writer.BitPosition;
            writer.Write(state);
            int endBit = writer.BitPosition;
            if (logger.LogEnabled()) logger.Log($"WriteState: {((endBit - startBit) + 7) / 8} bytes, Object:{behaviour.name}, Type:{behaviour.GetType()}");
        }

        void IServerController.ReadInput(ServerManager.PlayerTimeTracker tracker, NetworkReader reader, int inputTick)
        {
            TInput input = reader.Read<TInput>();
            // if new, and after last sim
            if (inputTick > tracker.lastReceivedInput && inputTick > _manager.lastSim)
            {
                lastReceived = tracker.lastReceivedInput;
                _inputBuffer.Set(inputTick, input);
            }
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
