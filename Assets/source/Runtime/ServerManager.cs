/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System.Collections.Generic;
using System.Linq;
using JamesFrowen.CSP.Alloc;
using JamesFrowen.DeltaSnapshot;
using Mirage;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// Controls all objects on server
    /// </summary>
    internal sealed class ServerManager
    {
        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerManager");
        private static readonly ILogger verbose = LogFactory.GetLogger("JamesFrowen.CSP.ServerManager_Verbose", LogType.Exception);

        // keep track of player list ourselves, so that we can use the SendToMany that does not allocate
        private readonly List<INetworkPlayer> _players = new List<INetworkPlayer>();
        private readonly Dictionary<INetworkPlayer, PlayerTimeTracker> _playerTracker = new Dictionary<INetworkPlayer, PlayerTimeTracker>();
        private readonly NetworkWorld _world;
        private readonly PredictionTime _time;
        private readonly IPredictionSimulation _simulation;
        private readonly PredictionCollection _behaviours;
        private bool _hostMode;
        private readonly int _bufferSize;
        private readonly IAllocator _allocator;
        private readonly WorldSnapshot _worldSnapshot;
        private readonly StateSender _sender;
        private readonly ServerInputHandler _inputHandler;

        internal int _lastSim;

        public PlayerTimeTracker Debug_FirstPlayertracker => _playerTracker.Values.FirstOrDefault();
        public PredictionCollection Behaviours => _behaviours;

        internal void SetHostMode()
        {
            _hostMode = true;
            foreach (var behaviour in _behaviours.GetBehaviours())
            {
                behaviour.ServerController.SetHostMode();
            }
        }

        public ServerManager(
            IPredictionSimulation simulation,
            TickRunner tickRunner,
            PredictionTime time,
            NetworkWorld world,
            IAllocator allocator,
            IMessageReceiver messageReceiver,
            int bufferSize = PredictionManager.DEFAULT_BUFFER_SIZE
            )
        {
            _bufferSize = bufferSize;
            _allocator = allocator;
            _worldSnapshot = new WorldSnapshot(_allocator, bufferSize);
            _time = time;
            _behaviours = new PredictionCollection(_time);
            _simulation = simulation;
            tickRunner.OnTick += Tick;

            _world = world;
            _world.onSpawn += OnSpawn;
            _world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (var item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }

            _sender = new StateSender(_players, _playerTracker, _worldSnapshot, _allocator, _bufferSize);
            _inputHandler = new ServerInputHandler(_playerTracker, _world);

            messageReceiver.RegisterHandler<InputState>(HandleInput);
            messageReceiver.RegisterHandler<DeltaWorldStateFragmentedAck>(_sender.HandleFragmentedAck);
        }

        private void HandleInput(INetworkPlayer player, InputState message)
        {
            _inputHandler.HandleInput(player, message, _lastSim);
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
            if (logger.LogEnabled()) logger.Log($"OnSpawn for netId={identity.NetId} name={identity.name}");

            _behaviours.Add(identity, out var _, out var foundBehaviours);

            var snapshots = GetBehaviourCache<ISnapshotBehaviour>.GetBehaviours(identity);
            if (snapshots.Count == 0)
                return;

            // allocate here so that state can be used befor first tick
            // in first tick this state will be copied to the GroupSnapshot for that tick
            var snap = _worldSnapshot.CreateAndAdd(identity, snapshots.ToArray(), _allocator);
            snap.SetActivePtr(_time.Tick);

            foreach (var behaviour in foundBehaviours)
            {
                if (logger.LogEnabled()) logger.Log($"Found PredictionBehaviour for {identity.NetId} {behaviour.GetType().Name}");

                behaviour.ServerSetup(_bufferSize);
                if (_hostMode)
                    behaviour.ServerController.SetHostMode();
            }
        }

        private void OnUnspawn(uint netId, NetworkIdentity identity)
        {
            _behaviours.Remove(identity, out var _, out var _);
            _worldSnapshot.Remove(netId, true);
        }

        public void Tick(int tick)
        {
            if (verbose.LogEnabled()) verbose.Log($"Server tick {tick}");

            _time.Tick = tick;
            _time.Method = UpdateMethod.NetworkFixed;

            _worldSnapshot.CopyFromPreviousTick(tick);
            Simulate(tick);
            _lastSim = tick;
            _time.Method = UpdateMethod.None;

            _sender.SendState(tick);
        }

        public void Cleanup()
        {
            _worldSnapshot.Cleanup();
        }

        public void Simulate(int tick)
        {
            var updates = _behaviours.GetUpdates();
            var updateCount = updates.Count;
            for (var i = 0; i < updateCount; i++)
            {
                var update = updates[i];
                // if behaviour run full tick stuff, otherwise just call fixedupdate
                if (update is IPredictionBehaviour behaviour)
                    behaviour.ServerController.Tick(tick);
                else
                    update.NetworkFixedUpdate();
            }

            _simulation.Simulate(_time.FixedDeltaTime);

            var behaviours = _behaviours.GetBehaviours();
            var behaviourCount = behaviours.Count;
            for (var i = 0; i < behaviourCount; i++)
                behaviours[i].AfterTick();
        }
    }
}
