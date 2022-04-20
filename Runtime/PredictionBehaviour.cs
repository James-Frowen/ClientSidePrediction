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
        ClientController<TInput, TState> _clientController;
        ServerController<TInput, TState> _serverController;
        ServerManager _serverManager;
        ClientManager _clientManager;

        // annoying cs stuff to have internal property and interface
        internal IClientController ClientController => _clientController;
        internal IServerController ServerController => _serverController;
        IClientController IPredictionBehaviour.ClientController => _clientController;
        IServerController IPredictionBehaviour.ServerController => _serverController;

        internal ServerManager ServerManager => _serverManager;
        internal ClientManager ClientManager => _clientManager;
        ServerManager IPredictionBehaviour.ServerManager => _serverManager;
        ClientManager IPredictionBehaviour.ClientManager => _clientManager;

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
        /// <param name="previous">the previous valid input</param>
        /// <param name="previousTick">what tick the previous valid was</param>
        /// <param name="currentTick">the current missing tick</param>
        /// <returns></returns>
        public virtual TInput MissingInput(TInput previous, int previousTick, int currentTick)
        {
            // default is just to return previous input.
            // chances are that the player is pressing the same keys as they were last frame

            // for example they press space for jump, that will be true for multiple frames
            // this should be used without ApplyInputs to check if jump key pressed this tick but not previous

            return previous;
        }

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
        /// Used to disable ResimulationTransition
        /// <para>ResimulationTransition requires the state to be gathered before and after resimulation. set this property to false to avoid that</para>
        /// </summary>
        public virtual bool EnableResimulationTransition => true;

        /// <summary>
        /// Used to smooth movement on client after Resimulation
        /// <para>Call <see cref="ApplyState"/> using to set new position or Leave empty function for no smoothing</para>
        /// </summary>
        /// <param name="before">state before resimulation</param>
        /// <param name="after">state after resimulation</param>
        public virtual void ResimulationTransition(TState before, TState after)
        {
            // by default nothing
            // after state will already be applied nothing needs to happen

            // you can override this function to apply moving between state before-re-simulatution and after.
        }


        void IPredictionBehaviour.ServerSetup(ServerManager serverManager, IPredictionTime time)
        {
            PredictionTime = time;
            _serverManager = serverManager;
            _serverController = new ServerController<TInput, TState>(ServerManager, this, Helper.BufferSize);
        }
        void IPredictionBehaviour.ClientSetup(ClientManager clientManager, IPredictionTime time)
        {
            PredictionTime = time;
            _clientManager = clientManager;
            _clientController = new ClientController<TInput, TState>(this, Helper.BufferSize);
        }

        void IPredictionBehaviour.CleanUp()
        {
            PredictionTime = null;
            _serverController = null;
            _clientController = null;
            _serverManager = null;
            _clientManager = null;
        }
    }

    internal static class PredictionBehaviourExtensions
    {
        /// <summary>
        /// Does the objects have inputs, and have control (eg server or authority)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="behaviour"></param>
        /// <returns></returns>
        public static bool UseInputs(this IPredictionBehaviour behaviour)
        {
            var nb = (NetworkBehaviour)behaviour;

            // if no inputs implemented, then just return early
            if (!behaviour.HasInput)
                return false;

            // is server and object has an owner
            // note: this mean un-owned objects can't be controlled by anyone expect the server
            if (nb.IsServer)
            {
                return nb.Owner != null;
            }
            // is client and has authority over the object, like the player object
            else if (nb.IsClient)
            {
                return nb.HasAuthority;
            }

            return false;
        }

        /// <summary>
        /// Does the objects have inputs, and have control (eg server or authority)
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
            // note: this mean un-owned objects can't be controlled by anyone expect the server
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
