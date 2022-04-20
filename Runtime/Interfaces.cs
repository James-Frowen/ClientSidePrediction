/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.CSP
{
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
        float FixedTime { get; }

        /// <summary>
        /// Current tick for simulation
        /// <para>
        /// this will rewind when doing resimulation on client
        /// </para>
        /// </summary>
        int Tick { get; }

        /// <summary>
        /// Is the current fixed update a resimulation? or the first time tick
        /// </summary>
        bool IsResimulation { get; }
    }

    internal interface IClientController
    {
        void AfterResimulate();
        void BeforeResimulate();

        void ReceiveState(int tick, NetworkReader reader);
        void Simulate(int tick);
        void InputTick(int clientLastSim);
        void OnTickSkip();
    }

    internal interface IServerController
    {
        void Tick(int tick);
        void WriteState(NetworkWriter writer, int tick);
        void ReceiveHostInput<TInput>(int tick, TInput _input);
        void SetHostMode();
        void OnReceiveInput(int tick, ArraySegment<byte> payload);
    }

    public interface IDebugPredictionLocalCopy
    {
        IDebugPredictionLocalCopy Copy { get; set; }

        void Setup(IPredictionTime time);
        void NoNetworkApply(object input);
    }

    public interface IDebugPredictionAfterImage
    {
        void CreateAfterImage(object state, Color color);
    }

    internal interface IPredictionBehaviour
    {
        IServerController ServerController { get; }
        IClientController ClientController { get; }
        bool HasInput { get; }

        void ServerSetup(IPredictionTime time);
        void ClientSetup(IPredictionTime time);
        void CleanUp();
    }

    public interface ISnapShotGeneration<TState>
    {
        TState GatherState();
        void ApplyState(TState state);
    }

    public interface ISnapshotDisposer<TState>
    {
        void DisposeState(TState state);
    }
}
