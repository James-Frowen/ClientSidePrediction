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
using UnityEngine;


namespace JamesFrowen.CSP
{
    /// <summary>
    /// IMPORTANT: PredictionManager should be in the same scene that is being similated. It should have local physice mode
    /// </summary>
    public class PredictionManager : MonoBehaviour
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.PredictionManager");

        public NetworkServer Server;
        public NetworkClient Client;
        public TickRunner tickRunner;
        public int clientDelay = 2;

        ClientManager clientManager;
        ServerManager serverManager;

        private void OnValidate()
        {
            if (clientManager != null)
                clientManager.ClientDelay = 2;
        }

        private void Awake()
        {
            Server?.Started.AddListener(ServerStarted);
            Client?.Started.AddListener(ClientStarted);
            tickRunner.onTick += Tickrunner_onTick;
        }

        public void ServerStarted()
        {
            // todo maybe use Physics.defaultPhysicsScene for non-local physics

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            // just pass in players collection, later this could be changed for match based stuff where you just pass in players for a match
            IReadOnlyCollection<INetworkPlayer> players = Server.Players;
            serverManager = new ServerManager(players, physics, tickRunner, Server.World);
        }

        public void ClientStarted()
        {
            if (Server != null && Server.Active) throw new NotSupportedException("Host mode not supported");
            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            clientManager = new ClientManager(physics, tickRunner, Client.World, Client.MessageHandler);
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
