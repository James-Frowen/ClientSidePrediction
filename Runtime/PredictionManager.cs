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
using JamesFrowen.CSP.Simulations;
using Mirage;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP
{
    [Serializable]
    public class ClientTickSettings
    {
        public int clientDelay = 2;
        public float diffThreshold = 0.5f;
        public float timeScaleModifier = 0.01f;
        public float skipThreshold = 10f;
        public int movingAverageCount = 30;
    }
    public class PredictionManager : MonoBehaviour
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.PredictionManager");

        [Header("References")]
        public NetworkServer Server;
        public NetworkClient Client;

        [Header("Simulation")]
        public SimulationMode physicsMode;

        [Header("Tick Settings")]
        [SerializeField] internal float TickRate = 50;
        [SerializeField] ClientTickSettings ClientTickSettings = new ClientTickSettings();

        ClientManager clientManager;
        ServerManager serverManager;

        TickRunner _tickRunner;
        IPredictionSimulation _simulation;

        /// <summary>
        /// Used to set custom Simulation or to set default simulation with different local physics scene
        /// </summary>
        /// <param name="simulation"></param>
        public void SetPredictionSimulation(IPredictionSimulation simulation)
        {
            if (serverManager != null) throw new InvalidOperationException("Can't set simulation after server has already started");
            if (clientManager != null) throw new InvalidOperationException("Can't set simulation after client has already started");

            _simulation = simulation;
        }

        private void Start()
        {
            if (_simulation == null)
                _simulation = new DefaultPredictionSimulation(physicsMode, gameObject.scene);

            Server?.Started.AddListener(ServerStart);
            Client?.Started.AddListener(ClientStart);
        }

        public void ServerStart()
        {
            _tickRunner = new TickRunner()
            {
                TickRate = TickRate
            };

            // just pass in players collection, later this could be changed for match based stuff where you just pass in players for a match
            IReadOnlyCollection<INetworkPlayer> players = Server.Players;

            serverManager = new ServerManager(players, _simulation, _tickRunner, Server.World);
        }

        public void ClientStart()
        {
            bool hostMode = Client.IsLocalClient;

            if (hostMode)
            {
                serverManager.SetHostMode();
                // todo clean up host stuff in ClientManager
                // todo add throw check inside ClientManager/clientset up to throw if server is active (host mode just uses server controller+behaviour)
                //clientManager = new ClientManager(hostMode, _simulation, _tickRunner, Client.World, Client.MessageHandler);
            }
            else
            {
                var clientRunner = new ClientTickRunner(Client.World.Time,
                    diffThreshold: ClientTickSettings.diffThreshold,
                    timeScaleModifier: ClientTickSettings.timeScaleModifier,
                    skipThreshold: ClientTickSettings.skipThreshold,
                    movingAverageCount: ClientTickSettings.movingAverageCount
                    )
                {
                    TickRate = TickRate,
                    ClientDelay = ClientTickSettings.clientDelay,
                };
                clientManager = new ClientManager(_simulation, clientRunner, Client.World, Client.MessageHandler);
                _tickRunner = clientRunner;
            }
        }

        private void Update()
        {
            _tickRunner?.OnUpdate();
        }
    }
}
