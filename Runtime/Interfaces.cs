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
        void WriteState(NetworkWriter writer);
        void ReceiveHostInput<TInput>(int tick, TInput _input) where TInput : IInputState;
        void SetHostMode();
        void OnReceiveInput(int tick, ArraySegment<byte> payload);
    }
    public interface IInputState
    {
        // todo use nullable instead of bool here
        // we just need valid to know if server received previous input
        // we also need to clear old valid because ring buffer
        bool Valid { get; set; }
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
}
