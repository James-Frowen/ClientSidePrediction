/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using JamesFrowen.DeltaSnapshot;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// Used to run physics simulate
    /// </summary>
    public interface IPredictionSimulation
    {
        void Simulate(float fixedDelta);
    }

    public interface IPredictionTime
    {
        /// <summary>
        /// Fixed interval between ticks
        /// </summary>
        float FixedDeltaTime { get; }

        /// <summary>
        /// Current time for simulation
        /// <para>
        /// this will rewind when doing resimulation on client
        /// </para>
        /// </summary>
        double UnscaledTime { get; }

        /// <summary>
        /// Current time for simulation
        /// <para>
        /// this will rewind when doing resimulation on client
        /// </para>
        /// </summary>
        float FixedTime { get; }

        /// <summary>
        /// Current tick for simulation
        /// <para>
        /// this will rewind when doing resimulation on client
        /// </para>
        /// </summary>
        int Tick { get; }

        /// <summary>
        /// Variable time that is in liine with fixed ticks, Similar to unity's <see cref="Time.time"/>
        /// </summary>
        double Time { get; }
        /// <summary>
        /// Amount <see cref="Time"/> changed this update
        /// </summary>
        double DeltaTime { get; }

        /// <summary>
        /// Is the current fixed update a resimulation? or the first time tick
        /// </summary>
        bool IsResimulation { get; }

        /// <summary>
        /// What CSP method is current being invoked.
        /// <para>Can be used to validate where code is running</para>
        /// </summary>
        UpdateMethod Method { get; }
    }

    /// <summary>
    /// What update method is currently being invoked
    /// </summary>
    public enum UpdateMethod
    {
        /// <summary>
        /// None of the CSP methods, could be any other unity update
        /// </summary>
        None,
        /// <summary>
        /// Includes all methods called as party of simulation and resimulation. (eg BeforeSimulate/AfterTick/etc)
        /// </summary>
        NetworkFixed,
        Input,
        Visual,
    }

    internal interface IClientController
    {
        void AfterResimulate();
        void BeforeResimulate();

        void Simulate(int tick);
        void InputTick(int tick);
        void WriteInput(NetworkWriter writer, int tick);
    }

    internal interface IServerController
    {
        void Tick(int tick);

        void ReceiveHostInput<TInput>(int tick, TInput _input);
        void SetHostMode();
        void ReadInput(PlayerTimeTracker tracker, NetworkReader reader, int inputTick, int lastSimulation);
    }

    public interface IDebugPredictionLocalCopy
    {
        IDebugPredictionLocalCopy Copy { get; set; }

        void Setup(IPredictionTime time);
        void NoNetworkApply(object input);
    }

    public interface IDebugPredictionAfterImage
    {
        bool ShowAfterImage { get; }
        unsafe void CreateAfterImage(ResimulationSnapshot state, Color color);
    }

    public interface IPredictionUpdates
    {
        /// <summary>
        /// What order callbacks should be called for.
        /// <para>Lower numbers will be called first</para>
        /// </summary>
        int Order { get; }

        IPredictionTime PredictionTime { get; set; }

        // todo, rename to early/late
        void InputUpdate();
        void NetworkFixedUpdate();
        void VisualUpdate();
    }

    internal interface IPredictionBehaviour : IPredictionUpdates
    {
        IServerController ServerController { get; }
        IClientController ClientController { get; }

        bool HasInput { get; }

        /// <summary>
        /// Called after state value has been changed
        /// <para>used to update any non-network state, like transform or rigidbody</para>
        /// </summary>
        void AfterStateChanged();

        /// <summary>
        /// Called after FixedUpdate and Physics.sim
        /// <para>use to update state from any non-network state, like using transform or rigidbody to set state.position</para>
        /// </summary>
        void AfterTick();

        void ServerSetup(int bufferSize);
        void ClientSetup(int bufferSize, ClientInterpolation clientInterpolation);
        void CleanUp();
    }
}
