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
    public class PredictionManager : MonoBehaviour
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.PredictionManager");

        public SimuationMode physicsMode;
        public NetworkServer Server;
        public NetworkClient Client;
        public TickRunner tickRunner;
        public int clientDelay = 2;

        ClientManager clientManager;
        ServerManager serverManager;

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

        private void OnValidate()
        {
            if (clientManager != null)
                clientManager.ClientDelay = clientDelay;
        }

        private void Start()
        {
            Server?.Started.AddListener(ServerStarted);
            Client?.Started.AddListener(ClientStarted);
            tickRunner.onTick += Tickrunner_onTick;
            if (_simulation == null)
                _simulation = new DefaultPredictionSimulation(physicsMode, gameObject.scene);
        }

        public void ServerStarted()
        {
            // just pass in players collection, later this could be changed for match based stuff where you just pass in players for a match
            IReadOnlyCollection<INetworkPlayer> players = Server.Players;
            serverManager = new ServerManager(players, _simulation, tickRunner, Server.World);
        }

        public void ClientStarted()
        {
            if (Server != null && Server.Active) throw new NotSupportedException("Host mode not supported");
            clientManager = new ClientManager(_simulation, tickRunner, Client.World, Client.MessageHandler);
            clientManager.ClientDelay = clientDelay;
        }

        private void Tickrunner_onTick(int tick)
        {
            // todo host mode support
            if (Server != null && Server.Active)
            {
                serverManager.Tick(tick);
            }

            if (Client != null && Client.Active)
            {
                clientManager.Tick(tick);
            }
        }
    }
}
