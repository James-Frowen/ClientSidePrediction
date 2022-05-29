/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using JamesFrowen.CSP.Simulations;
using Mirage;
using Mirage.Logging;
using UnityEngine;
using UnityEngine.Serialization;

namespace JamesFrowen.CSP
{
    [Serializable]
    public class ClientTickSettings
    {
        public float diffThreshold = 1.5f;
        public float timeScaleModifier = 0.01f;
        public float skipThreshold = 10f;
        public int movingAverageCount = 25;
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
        public float TickRate = 50;
        [Tooltip("How Often to send pings, used to make sure inputs are delay by correct amount")]
        public float PingInterval = 0.2f;
        [FormerlySerializedAs("ClientTickSettings")]
        [SerializeField] ClientTickSettings _clientTickSettings = new ClientTickSettings();

        [Header("Debug")]
        public TickDebuggerOutput DebugOutput;

        ClientManager clientManager;
        ServerManager serverManager;

        TickRunner _tickRunner;
        IPredictionSimulation _simulation;

        public TickRunner TickRunner => _tickRunner;

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

            Server?.Started.AddListener(ServerStarted);
            Server?.Stopped.AddListener(ServerStopped);
            Client?.Started.AddListener(ClientStarted);
            Client?.Disconnected.AddListener(ClientStopped);
        }
        private void OnDestroy()
        {
            // clean up if this object is destroyed
            ServerStopped();
            ClientStopped(default);
        }

        void ServerStarted()
        {
            _tickRunner = new TickRunner()
            {
                TickRate = TickRate
            };

            serverManager = new ServerManager(_simulation, _tickRunner, Server.World);
            Server.MessageHandler.RegisterHandler<InputState>(serverManager.HandleInput);

            // we need to add players because serverManager keeps track of a list internally
            Server.Connected.AddListener(serverManager.AddPlayer);
            Server.Disconnected.AddListener(serverManager.RemovePlayer);

            foreach (INetworkPlayer player in Server.Players)
                serverManager.AddPlayer(player);
        }

        void ServerStopped()
        {
            // if null, nothing to clean up
            if (serverManager == null)
                return;

            foreach (NetworkIdentity obj in Server.World.SpawnedIdentities)
            {
                if (obj.TryGetComponent(out IPredictionBehaviour behaviour))
                    behaviour.CleanUp();
            }

            // make sure to remove listens before setting to null
            Server.Connected.RemoveListener(serverManager.AddPlayer);
            Server.Disconnected.RemoveListener(serverManager.RemovePlayer);

            _tickRunner = null;
            serverManager = null;
        }

        void ClientStarted()
        {
            bool hostMode = Client.IsLocalClient;

            if (hostMode)
            {
                serverManager.SetHostMode();

                // todo dont send world state to host
                Client.MessageHandler.RegisterHandler<WorldState>((msg) => { });

                // todo clean up host stuff in ClientManager
                // todo add throw check inside ClientManager/clientset up to throw if server is active (host mode just uses server controller+behaviour)
                //clientManager = new ClientManager(hostMode, _simulation, _tickRunner, Client.World, Client.MessageHandler);
            }
            else
            {
                Client.World.Time.PingInterval = PingInterval;

                var clientRunner = new ClientTickRunner(
                    diffThreshold: _clientTickSettings.diffThreshold,
                    timeScaleModifier: _clientTickSettings.timeScaleModifier,
                    skipThreshold: _clientTickSettings.skipThreshold,
                    movingAverageCount: _clientTickSettings.movingAverageCount
                    )
                {
                    TickRate = TickRate,
                };
                clientManager = new ClientManager(_simulation, clientRunner, Client.World, Client.Player, Client.MessageHandler);
                _tickRunner = clientRunner;
            }
        }

        void ClientStopped(ClientStoppedReason _)
        {
            // todo, can we just have the `clientManager == null)` check below?
            // nothing to clean up if hostmode
            if (Server != null && Server.Active)
                return;
            // if null, nothing to clean up
            if (clientManager == null)
                return;

            foreach (NetworkIdentity obj in Client.World.SpawnedIdentities)
            {
                if (obj.TryGetComponent(out IPredictionBehaviour behaviour))
                    behaviour.CleanUp();
            }
            _tickRunner = null;
            clientManager = null;
        }

        private void Update()
        {
            _tickRunner?.OnUpdate();
#if DEBUG
            SetGuiValues();
#endif
        }

#if DEBUG
        private void SetGuiValues()
        {
            if (TickRunner != null && DebugOutput != null)
            {
                DebugOutput.IsServer = Server != null && Server.Active;
                DebugOutput.IsClient = (Client != null && Client.Active) && !(Server != null && Server.Active);

                if (DebugOutput.IsServer)
                {
                    DebugOutput.ClientTick = serverManager.Debug_FirstPlayertracker?.lastReceivedInput ?? 0;
                    DebugOutput.ServerTick = TickRunner.Tick;
                    DebugOutput.Diff = DebugOutput.ClientTick - DebugOutput.ServerTick;
                }
                if (DebugOutput.IsClient)
                {
                    DebugOutput.ClientTick = TickRunner.Tick;
                    DebugOutput.ServerTick = clientManager.Debug_ServerTick;
                    DebugOutput.Diff = DebugOutput.ClientTick - DebugOutput.ServerTick;
                }


                if (DebugOutput.IsClient)
                {
                    var clientRunner = (ClientTickRunner)TickRunner;
                    DebugOutput.ClientTimeScale = clientRunner.TimeScale;
                    DebugOutput.ClientDelayInTicks = clientRunner.Debug_DelayInTicks;
                    (float average, float stdDev) = clientRunner.Debug_RTT.GetAverageAndStandardDeviation();
                    DebugOutput.ClientRTT = average;
                    DebugOutput.ClientJitter = stdDev;
                }
            }
        }
#endif
    }
}
