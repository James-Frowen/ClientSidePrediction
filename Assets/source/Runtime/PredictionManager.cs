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
using JamesFrowen.CSP.Alloc;
using JamesFrowen.CSP.Debugging;
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
        public const int DEFAULT_BUFFER_SIZE = 64;

        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.PredictionManager");

        [Header("References")]
        public NetworkServer Server;
        public NetworkClient Client;

        [Header("Simulation")]
        [Tooltip("Should the timer automatically start when server/client, or should it wait for SetServerRunning/SetClientReady to be called manually")]
        public bool AutoStart = true;

        [FormerlySerializedAs("physicsMode")]
        public SimulationMode PhysicsMode;

        [Header("Tick Settings")]
        public float TickRate = 50;
        [Tooltip("How Often to send pings, used to make sure inputs are delay by correct amount")]
        public float PingInterval = 0.2f;
        [FormerlySerializedAs("ClientTickSettings")]
        [SerializeField] private ClientTickSettings _clientTickSettings = new ClientTickSettings();

        [Header("Debug")]
        public TickDebuggerOutput DebugOutput;

        //
        private ClientManager clientManager;
        private ServerManager serverManager;
        private TickRunner _tickRunner;
        private IPredictionSimulation _simulation;
        private SimpleAlloc _simpleAlloc;
        private bool _clientReady;
        private bool _serverRunning;
        private PredictionTime _time;

        public TickRunner TickRunner => _tickRunner;
        public IPredictionTime Time => _time;

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
            _simpleAlloc = new SimpleAlloc();

            if (_simulation == null)
                _simulation = new DefaultPredictionSimulation(PhysicsMode, gameObject.scene);

            if (Server != null)
            {
                Server.Started.AddListener(ServerStarted);
                Server.Stopped.AddListener(ServerStopped);
                Server.ManualUpdate = false;
            }

            if (Client != null)
            {
                Client.Started.AddListener(ClientStarted);
                Client.Disconnected.AddListener(ClientStopped);
                Client.ManualUpdate = false;
            }
        }

        private void OnDestroy()
        {
            // clean up if this object is destroyed
            ServerStopped();
            ClientStopped(default);

            _simpleAlloc?.Dispose();
        }

        private void ServerStarted()
        {
            _tickRunner = new TickRunner()
            {
                TickRate = TickRate
            };
            _time = new PredictionTime(_tickRunner);

            serverManager = new ServerManager(_simulation, _tickRunner, _time, Server.World, _simpleAlloc, Server.MessageHandler);

            serverManager.Behaviours.Add(UniTaskExtras.CustomTimingHelper.Init());

            // we need to add players because serverManager keeps track of a list internally
            Server.Authenticated.AddListener(serverManager.AddPlayer);
            Server.Disconnected.AddListener(serverManager.RemovePlayer);

            _tickRunner.BeforeAllTicks += Server.UpdateReceive;
            _tickRunner.AfterAllTicks += Server.UpdateSent;

            foreach (var player in Server.AuthenticatedPlayers)
                serverManager.AddPlayer(player);

            SetServerRunning(AutoStart || _serverRunning);
        }

        private void ServerStopped()
        {
            // if null, nothing to clean up
            if (serverManager == null)
                return;

            foreach (var obj in Server.World.SpawnedIdentities)
            {
                if (obj.TryGetComponent(out IPredictionBehaviour behaviour))
                    behaviour.CleanUp();
            }

            // make sure to remove listens before setting to null
            Server.Authenticated.RemoveListener(serverManager.AddPlayer);
            Server.Disconnected.RemoveListener(serverManager.RemovePlayer);

            _tickRunner = null;
            serverManager = null;
        }

        private void ClientStarted()
        {
            var hostMode = Client.IsHost;

            if (hostMode)
            {
                serverManager.SetHostMode();

                // todo dont send world state to host
                Client.MessageHandler.RegisterHandler<DeltaWorldState>((msg) => { });

                // todo clean up host stuff in ClientManager
                // todo add throw check inside ClientManager/clientset up to throw if server is active (host mode just uses server controller+behaviour)
                //clientManager = new ClientManager(hostMode, _simulation, _tickRunner, Client.World, Client.MessageHandler);

                AddClientEvents(serverManager.Behaviours);
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
                _tickRunner = clientRunner;
                _time = new PredictionTime(_tickRunner);
                clientManager = new ClientManager(_simulation, clientRunner, _time, Client.World, Client.Player, Client.MessageHandler, _simpleAlloc);
                AddClientEvents(clientManager.Behaviours);

                clientManager.Behaviours.Add(UniTaskExtras.CustomTimingHelper.Init());

                SetClientReady(AutoStart || _clientReady);
            }
        }

        private void AddClientEvents(PredictionCollection behaviours)
        {
            Client.ManualUpdate = true;

            _tickRunner.BeforeAllTicks += () =>
            {
                Client.UpdateReceive();
                InputUpdate(behaviours.GetUpdates());
            };
            _tickRunner.AfterAllTicks += () =>
            {
                VisualUpdate(behaviours.GetUpdates());
                Client.UpdateSent();
            };
        }

        private void ClientStopped(ClientStoppedReason _)
        {
            // todo, can we just have the `clientManager == null)` check below?
            // nothing to clean up if hostmode
            if (Server != null && Server.Active)
                return;
            // if null, nothing to clean up
            if (clientManager == null)
                return;

            foreach (var obj in Client.World.SpawnedIdentities)
            {
                if (obj.TryGetComponent(out IPredictionBehaviour behaviour))
                    behaviour.CleanUp();
            }
            _tickRunner = null;
            clientManager = null;
        }

        /// <summary>
        /// Sets if client is ready to send inputs and receive world state
        /// </summary>
        /// <param name="ready"></param>
        public void SetClientReady(bool ready)
        {
            if (logger.LogEnabled()) logger.Log($"SetClientReady: {ready}");

            if (Client.IsHost)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"SetClientReady does nothing in host mode and should not be called");
                return;
            }

            // store bool incase clientManager isn't created yet
            _clientReady = ready;
            if (clientManager != null)
            {
                clientManager.ReadyForWorldState = ready;
                _tickRunner.SetRunning(ready);
            }

            if (ready && _tickRunner != null)
            {
                ((ClientTickRunner)_tickRunner).ResetTime();
            }
        }

        /// <summary>
        /// Sets if server should be running tick and simulation
        /// <para>while this is false tickrunner will be spawned</para>
        /// </summary>
        public void SetServerRunning(bool running)
        {
            if (logger.LogEnabled()) logger.Log($"SetServerRunning: {running}");

            // store bool incase serverManager isn't created yet
            _serverRunning = running;
            _tickRunner.SetRunning(running);
        }

        internal void InputUpdate(IReadOnlyList<IPredictionUpdates> behaviours)
        {
            //Debug.Assert(behaviours != null, "Collection null");

            _time.Method = UpdateMethod.Input;
            for (var i = 0; i < behaviours.Count; i++)
            {
                try
                {
                    var behaviour = behaviours[i];
                    //Debug.Assert(behaviour != null, "Behaviour null");

                    behaviour.InputUpdate();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            _time.Method = UpdateMethod.None;
        }
        internal void VisualUpdate(IReadOnlyList<IPredictionUpdates> behaviours)
        {
            _time.Method = UpdateMethod.Visual;
            for (var i = 0; i < behaviours.Count; i++)
            {
                try
                {
                    var behaviour = behaviours[i];
                    behaviour.VisualUpdate();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            _time.Method = UpdateMethod.None;
        }

        private void Update()
        {
            // manaully update if tickRunner is null or not running
            if (_tickRunner == null || !_tickRunner.IsRunning)
            {
                Server?.UpdateReceive();
                Server?.UpdateSent();
                Client?.UpdateReceive();
                Client?.UpdateSent();
            }


            _tickRunner?.OnUpdate();
            serverManager?.Cleanup();
            clientManager?.Cleanup();

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
                DebugOutput.IsClient = Client != null && Client.Active && !(Server != null && Server.Active);

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
                    DebugOutput.ClientTimeScale = clientRunner.TimeScaleMultiple;
                    DebugOutput.ClientDelayInTicks = clientRunner.Debug_DelayInTicks;
                    (var average, var stdDev) = clientRunner.Debug_RTT.GetAverageAndStandardDeviation();
                    DebugOutput.ClientRTT = average;
                    DebugOutput.ClientJitter = stdDev;
                }
            }
        }
#endif
    }
}
