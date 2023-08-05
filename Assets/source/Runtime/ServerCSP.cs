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
using JamesFrowen.DeltaSnapshot;
using Mirage;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP
{
    internal sealed class ServerCSP
    {
        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerManager");
        private static readonly ILogger verbose = LogFactory.GetLogger("JamesFrowen.CSP.ServerManager_Verbose", LogType.Exception);

        /// <summary>
        /// Keep track of players, does not include host player
        /// </summary>
        private readonly List<INetworkPlayer> _remotePlayers = new List<INetworkPlayer>();
        private readonly NetworkWorld _world;
        private readonly PredictionTime _time;
        private readonly IPredictionSimulation _simulation;
        private readonly PredictionCollection _behaviours;
        private bool _hostMode;
        private readonly int _bufferSize;
        private readonly ServerInputHandler _inputHandler;

        internal int _lastSim;
        private int _lastSend;

        public PredictionCollection Behaviours => _behaviours;

        internal void SetHostMode()
        {
            _hostMode = true;
            foreach (var behaviour in _behaviours.GetBehaviours())
            {
                behaviour.ServerController.SetHostMode();
            }
        }

        public ServerCSP(
            IPredictionSimulation simulation,

            PredictionTime time,
            NetworkWorld world,
            IMessageReceiver messageReceiver,
            Dictionary<INetworkPlayer, PlayerTimeTracker> playerTrackers,
            int bufferSize
            )
        {
            _bufferSize = bufferSize;
            _time = time;
            _behaviours = new PredictionCollection(_time);
            _simulation = simulation;

            _world = world;
            _world.onSpawn += OnSpawn;
            _world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (var item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }

            _inputHandler = new ServerInputHandler(playerTrackers, _world);

            messageReceiver.RegisterHandler<InputState>(HandleInput);
            messageReceiver.RegisterHandler<InputStateNotReady>(_inputHandler.HandleNotReady);
        }

        private void HandleInput(INetworkPlayer player, InputState message)
        {
            _inputHandler.HandleInput(player, message, _lastSim);
        }

        private void OnSpawn(NetworkIdentity identity)
        {
            if (logger.LogEnabled()) logger.Log($"OnSpawn for netId={identity.NetId} name={identity.name}");

            _behaviours.Add(identity, out var _, out var foundBehaviours);

            // note: foundBehaviours could be empty, this is fine
            foreach (var behaviour in foundBehaviours)
            {
                if (logger.LogEnabled()) logger.Log($"Found PredictionBehaviour for {identity.NetId} {behaviour.GetType().Name}");

                behaviour.ServerSetup(_bufferSize);
                if (_hostMode)
                    behaviour.ServerController.SetHostMode();
            }
        }

        private void OnUnspawn(NetworkIdentity identity)
        {
            _behaviours.Remove(identity, out var _, out var _);
        }

        public void Tick(int tick)
        {
            if (verbose.LogEnabled()) verbose.Log($"Server tick {tick}");

            _time.Tick = tick;
            _time.Method = UpdateMethod.NetworkFixed;

            Simulate(tick);
            _lastSim = tick;
            _time.Method = UpdateMethod.None;
        }

        public void Simulate(int tick)
        {
            var updates = _behaviours.GetUpdates();
            var updateCount = updates.Count;
            for (var i = 0; i < updateCount; i++)
            {
                try
                {
                    var update = updates[i];
                    // if behaviour run full tick stuff, otherwise just call fixedupdate
                    if (update is IPredictionBehaviour behaviour)
                        behaviour.ServerController.Tick(tick);
                    else
                        update.NetworkFixedUpdate();
                }
                catch (Exception e)
                {
                    logger.LogException(e);
                }
            }

            _simulation.Simulate(_time.FixedDeltaTime);

            var behaviours = _behaviours.GetBehaviours();
            var behaviourCount = behaviours.Count;
            for (var i = 0; i < behaviourCount; i++)
                behaviours[i].AfterTick();
        }
    }
}
