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
using Mirage.SocketLayer;
using UnityEngine;


namespace JamesFrowen.CSP
{
    public interface IPredictionTime
    {
        float FixedDeltaTime { get; }
    }
    public interface IClientController
    {
        void AfterResimulate();
        void BeforeResimulate();

        void ReceiveState(int tick, NetworkReader reader);
        void Simulate(int tick);
        void InputTick(int clientLastSim);
    }
    public interface IServerController
    {
        void Tick(int tick);
        void WriteState(NetworkWriter writer);
    }
    public interface IInputState
    {
        bool Valid { get; }
    }
    public interface IDebugPredictionBehaviour
    {
        IDebugPredictionBehaviour Copy { get; set; }

        void NoNetworkApply(object input);
        void Setup(TickRunner runner);
        void CreateAfterImage(object state);
    }
    interface IPredictionBehaviour
    {
        IClientController ClientController { get; }
        IServerController ServerController { get; }

        void ServerSetup(IPredictionTime time);

        void ClientSetup(IPredictionTime time);
    }

    [NetworkMessage]
    struct WorldState
    {
        public int tick;
        public ArraySegment<byte> state;
    }

    /// <summary>
    /// IMPORTANT: PredictionManager should be in the same scene that is being similated. It should have local physice mode
    /// </summary>
    public class PredictionManager : MonoBehaviour
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.PredictionManager");

        public NetworkServer Server;
        public NetworkClient Client;
        public ClientManager clientManager;
        public ServerManager serverManager;
        public TickRunner tickRunner;
        public int clientDelay = 2;

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

    public abstract class PredictionBehaviour<TInput, TState> : NetworkBehaviour, IPredictionBehaviour where TInput : IInputState
    {
        public float ResimulateLerp = 0.1f;

        //TickRunner tickRunner;
        ClientController<TInput, TState> _client;
        ServerController<TInput, TState> _server;

        IClientController IPredictionBehaviour.ClientController => _client;
        IServerController IPredictionBehaviour.ServerController => _server;

        public abstract bool HasInput { get; }
        /// <summary>
        /// Called on Client to get inputs
        /// </summary>
        /// <returns></returns>
        public abstract TInput GetInput();
        /// <summary>
        /// Called on Server if inputs are missing
        /// </summary>
        /// <param name="previous"></param>
        /// <param name="previousTick"></param>
        /// <param name="currentTick"></param>
        /// <returns></returns>
        public abstract TInput MissingInput(TInput previous, int previousTick, int currentTick);
        /// <summary>
        /// Called on Before NetworkFixed
        /// </summary>
        /// <param name="input"></param>
        /// <param name="previous"></param>
        public abstract void ApplyInput(TInput input, TInput previous);

        public abstract void ApplyState(TState state);
        public abstract TState GatherState();
        public abstract void NetworkFixedUpdate(float fixedDelta);
        public abstract void ApplyStateLerp(TState a, TState b, float t);

        // todo generate by weaver
        protected abstract void RegisterInputMessage(NetworkServer server, Action<int, TInput[]> handler);
        public abstract void PackInputMessage(NetworkWriter writer, int tick, TInput[] inputs);

        void IPredictionBehaviour.ServerSetup(IPredictionTime time)
        {
            _server = new ServerController<TInput, TState>(this, time, Helper.BufferSize);

            // todo why doesn't IServer have message handler
            var networkServer = ((NetworkServer)Identity.Server);
            RegisterInputMessage(networkServer, (tick, inputs) => _server.OnReceiveInput(tick, inputs));
        }

        void IPredictionBehaviour.ClientSetup(IPredictionTime time)
        {
            _client = new ClientController<TInput, TState>(this, time, Helper.BufferSize);
        }
    }

    internal class Helper
    {
        public const int NO_VALUE = -1;


        // 256 is probably too bug, but is fine for example
        public const int BufferSize = 256;
        public static int TickToBuffer(int tick)
        {
            //negative
            if (tick < 0)
                tick += BufferSize;
            return tick % BufferSize;
        }
    }

    /// <summary>
    /// Controls all objects
    /// </summary>
    public class ClientManager
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientManager");
        private readonly PhysicsScene physics;
        private readonly IPredictionTime time;
        readonly NetworkTime networkTime;

        private readonly Dictionary<uint, IPredictionBehaviour> behaviours = new Dictionary<uint, IPredictionBehaviour>();

        int lastReceivedTick = Helper.NO_VALUE;
        bool unappliedTick;
        int lastSimTick;

        public int ClientDelay = 2;

        public ClientManager(PhysicsScene physics, IPredictionTime time, NetworkWorld world, MessageHandler messageHandler)
        {
            this.physics = physics;
            this.time = time;
            networkTime = world.Time;

            messageHandler.RegisterHandler<WorldState>(RecieveWorldState);
            world.onSpawn += OnSpawn;
            world.onUnspawn += OnUnspawn;

            // add existing items
            foreach (NetworkIdentity item in world.SpawnedIdentities)
            {
                OnSpawn(item);
            }
        }
        public void OnSpawn(NetworkIdentity identity)
        {
            if (identity.TryGetComponent(out IPredictionBehaviour behaviour))
            {
                behaviours.Add(identity.NetId, behaviour);
                behaviour.ClientSetup(time);
            }
        }
        public void OnUnspawn(NetworkIdentity identity)
        {
            behaviours.Remove(identity.NetId);
        }

        void RecieveWorldState(INetworkPlayer _, WorldState state)
        {
            ReceiveState(state.tick, state.state);
        }
        void ReceiveState(int tick, ArraySegment<byte> statePayload)
        {
            if (lastReceivedTick > tick)
            {
                logger.LogWarning("State out of order");
                return;
            }

            if (logger.LogEnabled()) logger.Log($"received STATE for {tick}");
            unappliedTick = true;
            lastReceivedTick = tick;
            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(statePayload))
            {
                while (reader.CanReadBytes(1))
                {
                    uint netId = reader.ReadPackedUInt32();
                    IPredictionBehaviour behaviour = behaviours[netId];
                    behaviour.ClientController.ReceiveState(tick, reader);
                }
            }
        }

        public void Resimulate(int receivedTick)
        {
            logger.Log($"Resimulate from {receivedTick} to {lastSimTick}");

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.BeforeResimulate();

            for (int tick = receivedTick; tick < lastSimTick; tick++)
            {
                // set forward Applying inputs
                // - exclude current tick, we will run this later
                if (tick - lastReceivedTick > Helper.BufferSize)
                    throw new OverflowException("Inputs overflowed buffer");

                foreach (IPredictionBehaviour behaviour in behaviours.Values)
                    behaviour.ClientController.Simulate(tick);
                physics.Simulate(time.FixedDeltaTime);
            }

            foreach (IPredictionBehaviour behaviour in behaviours.Values)
                behaviour.ClientController.AfterResimulate();
        }

        // todo add this tick delay stuff to tick runner rather than inside tick
        // maybe update tick/inputs at same interval as server
        // use tick rate stuff from snapshot interpolation

        // then use real time to send inputs to server??

        // maybe look at https://github.com/Unity-Technologies/FPSSample/blob/master/Assets/Scripts/Game/Main/ClientGameLoop.cs
        internal void Tick(int tick)
        {
            // delay from latency to make sure inputs reach server in time
            float tickDelay = getClientTick();

            int clientTick = tick + (int)Math.Floor(tickDelay);
            while (clientTick > lastSimTick)
            {
                // set lastSim to +1, so if we receive new snapshot, then we sim up to 106 again
                // we only want to step forward 1 tick at a time so we collect inputs, and sim correctly
                // todo: what happens if we do 2 at once, is that really a problem?
                lastSimTick++;

                if (logger.LogEnabled()) logger.Log($"Client tick {lastSimTick}, Client Delay {tickDelay:0.0}");

                if (unappliedTick)
                {
                    Resimulate(lastReceivedTick);
                    unappliedTick = false;
                }

                foreach (IPredictionBehaviour behaviour in behaviours.Values)
                {
                    // get and send inputs
                    behaviour.ClientController.InputTick(lastSimTick);
                    //apply 
                    behaviour.ClientController.Simulate(lastSimTick);
                }
                physics.Simulate(time.FixedDeltaTime);
            }
        }
        private float getClientTick()
        {
            // +2 to make sure inputs always get to server before simulation
            return ClientDelay + calculateTickDifference();
        }
        private float calculateTickDifference()
        {
            double oneWayTrip = networkTime.Rtt / 2;
            float tickTime = time.FixedDeltaTime;

            double tickDifference = oneWayTrip / tickTime;
            return (float)tickDifference;
        }
    }
    /// <summary>
    /// Controls 1 object
    /// </summary>
    /// <typeparam name="TInput"></typeparam>
    /// <typeparam name="TState"></typeparam>
    public class ClientController<TInput, TState> : IClientController where TInput : IInputState
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientController");

        readonly PredictionBehaviour<TInput, TState> behaviour;
        readonly IPredictionTime time;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;

        int lastReceivedTick = Helper.NO_VALUE;
        TState lastReceivedState;

        int lastSimTick;

        private int lastInputTick;
        Dictionary<int, TInput> pendingInputs = new Dictionary<int, TInput>();
        private TState beforeResimulateState;

        void IClientController.ReceiveState(int tick, NetworkReader reader)
        {
            TState state = reader.Read<TState>();
            if (lastReceivedTick > tick)
            {
                logger.LogWarning("State out of order");
                return;
            }

            if (logger.LogEnabled()) logger.Log($"received STATE for {tick}");
            lastReceivedTick = tick;
            lastReceivedState = state;
        }

        public ClientController(PredictionBehaviour<TInput, TState> behaviour, IPredictionTime time, int bufferSize)
        {
            this.behaviour = behaviour;
            _inputBuffer = new TInput[bufferSize];
            this.time = time;
        }

        void IClientController.BeforeResimulate()
        {
            // if receivedTick = 100
            // then we want to Simulate (100->101)
            // so we pass tick 100 into Simulate
            beforeResimulateState = behaviour.GatherState();

            // if lastSimTick = 105
            // then our last sim step will be (104->105)
            behaviour.ApplyState(lastReceivedState);

            if (behaviour is IDebugPredictionBehaviour debug)
                debug.CreateAfterImage(lastReceivedState);
        }
        void IClientController.AfterResimulate()
        {
            TState next = behaviour.GatherState();
            behaviour.ApplyStateLerp(beforeResimulateState, next, behaviour.ResimulateLerp);
            beforeResimulateState = default;
        }

        /// <summary>
        /// From tick N to N+1
        /// </summary>
        /// <param name="tick"></param>
        void IClientController.Simulate(int tick) => Simulate(tick);
        private void Simulate(int tick)
        {
            TInput input = GetInput(tick);
            TInput previous = GetInput(tick - 1);
            behaviour.ApplyInput(input, previous);
            behaviour.NetworkFixedUpdate(time.FixedDeltaTime);
        }

        void IClientController.InputTick(int tick)
        {
            if (!behaviour.HasInput)
                return;

            if (lastInputTick != tick - 1)
                throw new Exception("Inputs ticks called out of order");
            lastInputTick = tick;

            TInput thisTickInput = behaviour.GetInput();
            SetInput(tick, thisTickInput);
            SendInput(tick, thisTickInput);
        }

        void SendInput(int tick, TInput state)
        {
            if (behaviour is IDebugPredictionBehaviour debug)
                debug.Copy?.NoNetworkApply(state);

            pendingInputs.Add(tick, state);

            var inputs = new TInput[pendingInputs.Count];
            foreach (KeyValuePair<int, TInput> pending in pendingInputs)
            {
                inputs[tick - pending.Key] = pending.Value;
            }

            IConnection conn = behaviour.Client.Player.Connection;

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                behaviour.PackInputMessage(writer, tick, inputs);

                if (logger.LogEnabled()) logger.Log($"sending inputs for {tick}. length: {inputs.Length}");
                INotifyToken token = conn.SendNotify(writer.ToArraySegment());
                token.Delivered += () =>
                {
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        // once message is acked, remove all inputs starting at the tick
                        pendingInputs.Remove(tick - i);
                    }
                };
            }
        }
    }

    public class ServerManager
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerManager");
        private readonly IEnumerable<INetworkPlayer> players;
        private readonly PhysicsScene physics;
        private readonly IPredictionTime time;
        private readonly Dictionary<uint, IPredictionBehaviour> behaviours = new Dictionary<uint, IPredictionBehaviour>();

        public ServerManager(IEnumerable<INetworkPlayer> players, PhysicsScene physics, IPredictionTime time, NetworkWorld world)
        {
            this.players = players;
            this.physics = physics;
            this.time = time;

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
            if (identity.TryGetComponent(out IPredictionBehaviour behaviour))
            {
                behaviours.Add(identity.NetId, behaviour);
                behaviour.ServerSetup(time);
            }
        }
        private void OnUnspawn(NetworkIdentity identity)
        {
            behaviours.Remove(identity.NetId);
        }

        public void Tick(int tick)
        {
            if (logger.LogEnabled()) logger.Log($"Server tick {tick}");
            foreach (IPredictionBehaviour behaviour in behaviours.Values)
            {
                behaviour.ServerController.Tick(tick);
            }

            physics.Simulate(time.FixedDeltaTime);

            var msg = new WorldState() { tick = tick };
            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                foreach (KeyValuePair<uint, IPredictionBehaviour> kvp in behaviours)
                {
                    writer.WritePackedUInt32(kvp.Key);
                    IPredictionBehaviour behaviour = kvp.Value;
                    behaviour.ServerController.WriteState(writer);
                }

                msg.state = writer.ToArraySegment();
                NetworkServer.SendToMany(players, msg, Channel.Unreliable);
            }
        }
    }
    public class ServerController<TInput, TState> : IServerController where TInput : IInputState
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerController");

        readonly PredictionBehaviour<TInput, TState> behaviour;
        readonly IPredictionTime time;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;
        /// <summary>Clears Previous inputs for tick (eg tick =100 clears tick 99's inputs</summary>
        void ClearPreviousInput(int tick) => SetInput(tick - 1, default);

        int lastReceived = NEVER_RECEIVED;
        (int tick, TInput input) lastValidInput;
        const int NEVER_RECEIVED = -1;

        int lastSim;

        public ServerController(PredictionBehaviour<TInput, TState> behaviour, IPredictionTime time, int bufferSize)
        {
            this.behaviour = behaviour;
            this.time = time;
            _inputBuffer = new TInput[bufferSize];
        }

        void IServerController.WriteState(NetworkWriter writer)
        {
            TState state = behaviour.GatherState();
            writer.Write(state);
        }

        internal void OnReceiveInput(int tick, TInput[] newInputs)
        {
            // if lastTick is before last sim, then it is late and we can't use
            if (tick < lastSim)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSim}. late by {lastSim - tick}");
                return;
            }
            else
            {
                if (logger.LogEnabled()) logger.Log($"received inputs for {tick}. length: {newInputs.Length}, lastSim:{lastSim}. early by { tick - lastSim}");
            }

            for (int i = 0; i < newInputs.Length; i++)
            {
                int t = tick - i;
                TInput input = newInputs[i];
                // if new, and after last sim
                if (t > lastReceived && t > lastSim)
                {
                    Debug.Assert(input.Valid);
                    SetInput(t, input);
                }
            }

            lastReceived = Mathf.Max(lastReceived, tick);
        }

        void IServerController.Tick(int tick)
        {
            if (behaviour.HasInput)
                ApplyInputs(tick);

            behaviour.NetworkFixedUpdate(time.FixedDeltaTime);

            ClearPreviousInput(tick);
            lastSim = tick;
        }

        private void ApplyInputs(int tick)
        {
            // dont need to do anything till first is received
            if (lastReceived == NEVER_RECEIVED)
                return;

            TInput input = GetInput(tick);
            TInput previous = GetInput(tick - 1);
            if (input.Valid)
            {
                lastValidInput = (tick, input);
                behaviour.ApplyInput(input, previous);
            }
            else
            {
                if (logger.WarnEnabled()) logger.LogWarning($"No inputs for {tick}");
                // todo use last valid input instead of just using previois
                behaviour.MissingInput(lastValidInput.input, lastValidInput.tick, tick);
            }
        }
    }
}
