/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using Mirage;
using Mirage.Serialization;

namespace JamesFrowen.CSP
{
    public abstract class PredictionBehaviour<TInput, TState> : NetworkBehaviour, IPredictionBehaviour where TInput : IInputState
    {
        ClientController<TInput, TState> _client;
        ServerController<TInput, TState> _server;

        IClientController IPredictionBehaviour.ClientController => _client;
        IServerController IPredictionBehaviour.ServerController => _server;

        public IPredictionTime PredictionTime { get; set; }

        /// <summary>
        /// Used to disable input for this object
        /// <para>This should be false for non player objects</para>
        /// </summary>
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
        /// Applies state to the object
        /// </summary>
        /// <param name="state"></param>
        public abstract void ApplyState(TState state);
        /// <summary>
        /// Gets state from the object
        /// </summary>
        /// <returns></returns>
        public abstract TState GatherState();
        /// <summary>
        /// Apply inputs to object and modify the objects state.
        /// <para>Applies any physics/state logic to object here</para>
        /// <para>For example any custom gravity, drag, etc</para>
        /// <para>Called once per tick on server and client, and for each resimulation step on client</para>
        /// </summary>
        /// <param name="fixedDelta"></param>
        public abstract void NetworkFixedUpdate(TInput input, TInput previous);
        /// <summary>
        /// Used to smooth movement on client after Resimulation
        /// <para>Call <see cref="ApplyState"/> using to set new position or Leave empty function for no smoothing</para>
        /// </summary>
        /// <param name="before">state before resimulation</param>
        /// <param name="after">state after resimulation</param>
        public abstract void ResimulationTransition(TState before, TState after);

        // todo generate by weaver
        /// <summary>todo generate by weaver, Copy code from examples for now</summary>
        protected abstract void RegisterInputMessage(NetworkServer server, Action<INetworkPlayer, int, TInput[]> handler);
        /// <summary>todo generate by weaver, Copy code from examples for now</summary>
        public abstract void PackInputMessage(NetworkWriter writer, int tick, TInput[] inputs);

        void IPredictionBehaviour.ServerSetup(IPredictionTime time)
        {
            PredictionTime = time;
            _server = new ServerController<TInput, TState>(this, time, Helper.BufferSize);

            // todo why doesn't IServer have message handler
            var networkServer = ((NetworkServer)Identity.Server);
            RegisterInputMessage(networkServer, (player, tick, inputs) => _server.OnReceiveInput(player, tick, inputs));
        }
        void IPredictionBehaviour.ClientSetup(IPredictionTime time)
        {
            PredictionTime = time;
            _client = new ClientController<TInput, TState>(this, time, Helper.BufferSize);
        }
    }
}
