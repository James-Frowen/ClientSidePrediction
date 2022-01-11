/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using Mirage.Serialization;

namespace JamesFrowen.CSP
{
    public interface IPredictionSimulation
    {
        void Simulate(float fixedDelta);
    }
    public interface IPredictionTime
    {
        float FixedDeltaTime { get; }
        int Tick { get; }
    }
    public interface IClientController
    {
        void AfterResimulate();
        void BeforeResimulate();

        void ReceiveState(int tick, NetworkReader reader);
        void Simulate(int tick);
        void InputTick(int clientLastSim);
        void OnTickSkip();
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
        void Setup(float fixedDeltaTime);
        void CreateAfterImage(object state);
    }
    interface IPredictionBehaviour
    {
        IClientController ClientController { get; }
        IServerController ServerController { get; }

        void ServerSetup(IPredictionTime time);

        void ClientSetup(IPredictionTime time);
    }
}
