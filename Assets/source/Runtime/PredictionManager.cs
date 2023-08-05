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
using Cysharp.Threading.Tasks;
using JamesFrowen.CSP.Debugging;
using JamesFrowen.CSP.Simulations;
using JamesFrowen.DeltaSnapshot;
using JamesFrowen.DeltaSnapshot.Alloc;
using Mirage;
using Mirage.Logging;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Serialization;

namespace JamesFrowen.CSP
{
    public class PredictionManager : MonoBehaviour
    {
        public const int DEFAULT_BUFFER_SIZE = 64;

        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.PredictionManager");
        private static readonly ProfilerMarker updateMarker = new ProfilerMarker(ProfilerCategory.Network, "JamesFrowen.CSP.PredictionManager.Update");

        [Header("References")]
        [SerializeField] private NetworkServer _server;
        [SerializeField] private NetworkClient _client;
        private bool _addedServerEvents;
        private bool _addedClientEvents;

        [Header("Simulation")]
        [Tooltip("Should the timer automatically start when server/client, or should it wait for SetServerRunning/SetClientReady to be called manually")]
        public bool AutoStart = true;

        [FormerlySerializedAs("physicsMode")]
        public SimulationMode PhysicsMode;

        [Header("Tick Settings")]
        public float TickRate = 50;
        [Tooltip("How Often to send pings, used to make sure inputs are delay by correct amount")]

        [Header("Debug")]
        public TickDebuggerOutput DebugOutput;

        //
        private ClientCSP _clientCSP;
        private ClientDeltaSnapshot _clientDS;
        private ServerCSP _serverCSP;
        private ServerDeltaSnapshot _serverDS;
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
            if (_serverCSP != null) throw new InvalidOperationException("Can't set simulation after server has already started");
            if (_clientCSP != null) throw new InvalidOperationException("Can't set simulation after client has already started");

            _simulation = simulation;
        }

        private void Awake()
        {
            _simpleAlloc = new SimpleAlloc();

            if (_simulation == null)
                _simulation = new DefaultPredictionSimulation(PhysicsMode, gameObject.scene);
        }
        private void Start()
        {
            Setup(_server, _client);
        }

        public void Setup(NetworkServer server, NetworkClient client)
        {
            if (server != null)
            {
                if (logger.LogEnabled()) logger.Log($"Setting up server events");

                // remove old events, then add new
                if (_addedServerEvents)
                {
                    _server.Started.RemoveListener(ServerStarted);
                    _server.Stopped.RemoveListener(ServerStopped);
                }

                _server = server;

                _server.Started.AddListener(ServerStarted);
                _server.Stopped.AddListener(ServerStopped);
                _addedServerEvents = true;
            }

            if (client != null)
            {
                if (logger.LogEnabled()) logger.Log($"Setting up client events");

                if (_addedClientEvents)
                {
                    _client.Started.RemoveListener(ClientStarted);
                    _client.Disconnected.RemoveListener(ClientStopped);
                }

                _client = client;

                _client.Started.AddListener(ClientStarted);
                _client.Disconnected.AddListener(ClientStopped);
                _addedClientEvents = true;
            }
        }

        private void OnDestroy()
        {
            // clean up if this object is destroyed
            ServerStopped();
            ClientStopped(default);

            if (_addedServerEvents)
            {
                _server.Started.RemoveListener(ServerStarted);
                _server.Stopped.RemoveListener(ServerStopped);
            }

            if (_addedClientEvents)
            {
                _client.Started.RemoveListener(ClientStarted);
                _client.Disconnected.RemoveListener(ClientStopped);
            }

            _simpleAlloc?.Dispose();
        }

        private void ServerStarted()
        {
            _tickRunner = new TickRunner(TickRate);
            _time = new PredictionTime(_tickRunner);


            var socketFactory = _server.SocketFactory;
            var maxSize = socketFactory.MaxPacketSize;
            _serverDS = new ServerDeltaSnapshot(_server, _server.World, _simpleAlloc, _server.MessageHandler, maxSize, DEFAULT_BUFFER_SIZE);
            _serverCSP = new ServerCSP(_simulation, _time, _server.World, _server.MessageHandler, _serverDS.PlayerTracker, SnapshotManager.DEFAULT_BUFFER_SIZE);
            _tickRunner.OnTick += _serverCSP.Tick;

            _serverCSP.Behaviours.Add(UniTaskExtras.CustomTimingHelper.Init());

            _server.ManualUpdate = true;

            _tickRunner.BeforeAllTicks += _server.UpdateReceive;

            _tickRunner.BeforeTick += _serverDS.BeforeTick;
            _tickRunner.OnTick += _serverCSP.Tick;

            _tickRunner.AfterAllTicks += _serverDS.AfterAllTicks;
            _tickRunner.AfterAllTicks += _server.UpdateSent;

            SetServerRunning(AutoStart || _serverRunning);

            _server.MessageHandler.RegisterHandler<RequestTimeInfo>(HandleTimeInfo);
        }

        private void HandleTimeInfo(INetworkPlayer player, RequestTimeInfo message)
        {
            player.Send(new TimeInfo
            {
                ClientTime = message.ClientTime,
                Tick = _tickRunner.Tick,
                TimeScale = UnityEngine.Time.timeScale == 1 ? default(float?) : UnityEngine.Time.timeScale,
            });
        }

        private void ServerStopped()
        {
            // if null, nothing to clean up
            if (_serverCSP == null)
                return;

            foreach (var obj in _server.World.SpawnedIdentities)
            {
                if (obj.TryGetComponent(out IPredictionBehaviour behaviour))
                    behaviour.CleanUp();
            }

            // clear manual update so that message still work when prediction manager is destroyed
            _server.ManualUpdate = false;

            _tickRunner = null;
            _serverCSP = null;
        }

        private void ClientStarted()
        {
            var hostMode = _client.IsLocalClient;

            if (hostMode)
            {
                _serverCSP.SetHostMode();

                // todo clean up host stuff in ClientManager
                // todo add throw check inside ClientManager/clientset up to throw if server is active (host mode just uses server controller+behaviour)
                //clientManager = new ClientManager(hostMode, _simulation, _tickRunner, Client.World, Client.MessageHandler);

                AddClientEvents(_serverCSP.Behaviours);
            }
            else
            {
                if (logger.LogEnabled()) logger.Log($"Client started, setting up clientManager for prediction");

                var clientRunner = new ClientTickRunner(TickRate);
                _tickRunner = clientRunner;
                _time = new PredictionTime(_tickRunner);
                _clientDS = new ClientDeltaSnapshot(_client.Player, _client.World, _client.MessageHandler, _simpleAlloc, SnapshotManager.DEFAULT_BUFFER_SIZE);
                _clientCSP = new ClientCSP(_simulation, clientRunner, _time, _client.World, _client.Player, _clientDS, SnapshotManager.DEFAULT_BUFFER_SIZE);

                clientRunner.OnTick += _clientCSP.Tick;
                clientRunner.AfterAllTicks += _clientCSP.AfterAllTicks;
                clientRunner.OnTickSkip += _clientCSP.OnTickSkip;

                AddClientEvents(_clientCSP.Behaviours);

                _clientCSP.Behaviours.Add(UniTaskExtras.CustomTimingHelper.Init());

                SetClientReady(AutoStart || _clientReady);
            }
        }

        private void AddClientEvents(PredictionCollection behaviours)
        {
            _client.ManualUpdate = true;

            _tickRunner.BeforeAllTicks += () =>
            {
                _client.UpdateReceive();
                InputUpdate(behaviours.GetUpdates());
            };
            _tickRunner.AfterAllTicks += () =>
            {
                VisualUpdate(behaviours.GetUpdates());
                _client.UpdateSent();
            };
        }

        private void ClientStopped(ClientStoppedReason _)
        {
            // todo, can we just have the `clientManager == null)` check below?
            // nothing to clean up if hostmode
            if (_server != null && _server.Active)
                return;
            // if null, nothing to clean up
            if (_clientCSP == null)
                return;

            foreach (var obj in _client.World.SpawnedIdentities)
            {
                if (obj.TryGetComponent(out IPredictionBehaviour behaviour))
                    behaviour.CleanUp();
            }
            _tickRunner = null;
            _clientCSP = null;

            // clear manual update so that message still work when prediction manager is destroyed
            _client.ManualUpdate = false;
        }

        /// <summary>
        /// Sets if client is ready to send inputs and receive world state
        /// </summary>
        /// <param name="ready"></param>
        public void SetClientReady(bool ready)
        {
            if (logger.LogEnabled()) logger.Log($"SetClientReady: {ready}");

            if (_client.IsLocalClient)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"SetClientReady does nothing in host moode and should not be called");
                return;
            }

            // store bool incase clientManager isn't created yet
            _clientReady = ready;
            if (_clientCSP != null)
            {
                // set to not ready
                if (!ready)
                    _clientCSP.ReadyForWorldState = false;

                _tickRunner.SetRunning(ready);
            }

            if (ready && _tickRunner != null)
            {
                // reset time first, 
                ((ClientTickRunner)_tickRunner).ResetTime();

                SendRequetTimeInfo().Forget();
            }
        }

        private async UniTaskVoid SendRequetTimeInfo()
        {
            // create waiter
            var waiter = new MessageWaiter<TimeInfo>(_client);

            // send message
            _client.Send(new RequestTimeInfo
            {
                ClientTime = _time.UnscaledTime,
            });


            // wait for reply
            var (disconnect, msg) = await waiter.WaitAsync();

            if (disconnect)
                return;

            // set timescale
            if (msg.TimeScale.HasValue)
            {
                UnityEngine.Time.timeScale = msg.TimeScale.Value;
            }
            // else if no value, then reset scale to 1
            else if (UnityEngine.Time.timeScale != 1)
            {
                UnityEngine.Time.timeScale = 1;
            }

            // reset time
            ((ClientTickRunner)_tickRunner).ResetTime(msg);

            // only saym we are ready for world state after we have received reset
            _clientCSP.ReadyForWorldState = true;
        }

        /// <summary>
        /// Sets if server should be running tick and simulation
        /// <para>while this is false tickRunner will be spawned</para>
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
                    behaviours[i].InputUpdate();
                }
                catch (Exception e)
                {
                    logger.LogException(e);
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
                    behaviours[i].VisualUpdate();
                }
                catch (Exception e)
                {
                    logger.LogException(e);
                }
            }
            _time.Method = UpdateMethod.None;
        }

        private void Update()
        {
            updateMarker.Begin(this);

            // manaully update if tickRunner is null or not running
            if (_tickRunner == null || !_tickRunner.IsRunning || (_tickRunner is ClientTickRunner clientTickRunner && !clientTickRunner.Intialized))
            {
                _server?.UpdateReceive();
                _server?.UpdateSent();
                _client?.UpdateReceive();
                _client?.UpdateSent();
            }

            _tickRunner?.OnUpdate();

#if DEBUG
            SetGuiValues();
#endif
            updateMarker.End();
        }

#if DEBUG
        private void SetGuiValues()
        {
            if (TickRunner != null && DebugOutput != null)
            {
                DebugOutput.IsServer = _server != null && _server.Active;
                DebugOutput.IsClient = _client != null && _client.Active && !(_server != null && _server.Active);

                if (DebugOutput.IsServer)
                {
                    DebugOutput.ClientTick = _serverDS.Debug_FirstPlayerTracker?.lastReceivedInput ?? 0;
                    DebugOutput.ServerTick = TickRunner.Tick;
                    DebugOutput.Diff = DebugOutput.ClientTick - DebugOutput.ServerTick;
                }
                if (DebugOutput.IsClient)
                {
                    DebugOutput.ClientTick = TickRunner.Tick;
                    DebugOutput.ServerTick = _clientDS.LastReceivedTick ?? 0;
                    DebugOutput.Diff = DebugOutput.ClientTick - DebugOutput.ServerTick;
                }


                if (DebugOutput.IsClient)
                {
                    var clientRunner = (ClientTickRunner)TickRunner;

                    DebugOutput.ClientTimeScale = clientRunner.TimeScaleMultiple;
                    DebugOutput.ClientDelayInTicks = clientRunner.GetDelayInTicks();

                    var (rtt, jitter) = clientRunner.GetRTTAndJitter();
                    DebugOutput.ClientRTT = rtt;
                    DebugOutput.ClientJitter = jitter;
                }
            }
        }
#endif
    }
}
