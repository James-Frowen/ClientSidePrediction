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

namespace JamesFrowen.CSP
{
    /// <summary>
    /// Placeholder input for non-input class
    /// Use never be used by scripts
    /// </summary>
    public struct NoInputs { }

    /// <summary>
    /// Base class for Client side prediction for objects without input, like physics objects in a scene.
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    public abstract class PredictionBehaviour<TState> : PredictionBehaviourBase<NoInputs, TState>
    {
        public sealed override bool HasInput => false;
        public sealed override NoInputs GetInput() => throw new NotSupportedException();
        public sealed override NoInputs MissingInput(NoInputs previous, int previousTick, int currentTick) => throw new NotSupportedException();
        public sealed override void ApplyInputs(NoInputs input, NoInputs previous) => throw new NotSupportedException();
    }

    /// <summary>
    /// Base class for Client side prediction for objects with input, like player objects with movement.
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    public abstract class PredictionBehaviour<TInput, TState> : PredictionBehaviourBase<TInput, TState>
    {
        public sealed override bool HasInput => true;
    }

    public abstract class PredictionBehaviourBase<TInput, TState> : NetworkBehaviour, IPredictionBehaviour
    {
        ClientController<TInput, TState> _client;
        ServerController<TInput, TState> _server;

        // annoying cs stuff to have internal property and interface
        internal IClientController ClientController => _client;
        internal IServerController ServerController => _server;
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
        /// Called on Server and on clients with authority
        /// <para>Called before <see cref="NetworkFixedUpdate"/></para>
        /// </summary>
        /// <param name="input"></param>
        /// <param name="previous"></param>
        public abstract void ApplyInputs(TInput input, TInput previous);
        /// <summary>
        /// Modify the objects state. Called on all objects, use <see cref="ApplyInputs(TInput, TInput)"/> for effects on owned objects
        /// <para>Applies any physics/state logic to object here</para>
        /// <para>For example any custom gravity, drag, etc</para>
        /// <para>Called once per tick on server and client, and for each resimulation step on client</para>
        /// </summary>
        /// <param name="fixedDelta"></param>
        public abstract void NetworkFixedUpdate();
        /// <summary>
        /// Used to smooth movement on client after Resimulation
        /// <para>Call <see cref="ApplyState"/> using to set new position or Leave empty function for no smoothing</para>
        /// </summary>
        /// <param name="before">state before resimulation</param>
        /// <param name="after">state after resimulation</param>
        public abstract void ResimulationTransition(TState before, TState after);


        void IPredictionBehaviour.ServerSetup(IPredictionTime time)
        {
            PredictionTime = time;
            _server = new ServerController<TInput, TState>(this, Helper.BufferSize);
        }
        void IPredictionBehaviour.ClientSetup(IPredictionTime time)
        {
            PredictionTime = time;
            _client = new ClientController<TInput, TState>(this, Helper.BufferSize);
        }

        void IPredictionBehaviour.CleanUp()
        {
            PredictionTime = null;
            _server = null;
            _client = null;
        }
    }

    internal static class PredictionBehaviourExtensions
    {
        /// <summary>
        /// Does the objects have inputs, and have control (eg server or authority
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="behaviour"></param>
        /// <returns></returns>
        public static bool UseInputs<T>(this T behaviour) where T : NetworkBehaviour, IPredictionBehaviour
        {
            // if no inputs implemented, then just return early
            if (!behaviour.HasInput)
                return false;

            // is server and object has an owner
            // note: this mean un-owned objects can't be controled by anyone expect the server
            if (behaviour.IsServer)
            {
                return behaviour.Owner != null;
            }
            // is client and has authority over the object, like the player object
            else if (behaviour.IsClient)
            {
                return behaviour.HasAuthority;
            }

            return false;
        }
    }
}
