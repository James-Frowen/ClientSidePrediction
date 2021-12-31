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
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public interface IDebugPredictionBehaviour
    {
        IDebugPredictionBehaviour Copy { get; set; }

        void Setup(TickRunner runner);
    }
    public interface IPredictionBehaviour<TInput, TState>
    {
        void ApplyInput(TInput input, TInput previous);
        void Simulate();
        void SendState(int tick, TState p);
        TState GatherState();
        void ApplyState(TState lastRecievedState);
        TInput GetUnityInput();
        void SendInput(int tick, TInput thisTickInput);
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

    public class ClientController<TInput, TState>
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientController");

        readonly IPredictionBehaviour<TInput, TState> behaviour;
        readonly NetworkTime networkTime;
        readonly TickRunner tickRunner;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;

        public bool unappliedTick;
        public int lastRecievedTick = Helper.NO_VALUE;
        public TState lastRecievedState;

        public int lastSimTick;

        public void ReceiveState(int tick, TState state)
        {
            if (lastRecievedTick > tick)
            {
                logger.LogWarning("State out of order");
                return;
            }


            if (logger.LogEnabled()) logger.Log($"recieved STATE for {tick}");
            unappliedTick = true;
            lastRecievedTick = tick;
            lastRecievedState = state;

            Resimulate(tick);
        }


        public ClientController(IPredictionBehaviour<TInput, TState> behaviour, NetworkTime networkTime, TickRunner tickRunner, int bufferSize)
        {
            this.behaviour = behaviour;
            _inputBuffer = new TInput[bufferSize];
            this.networkTime = networkTime;
            this.tickRunner = tickRunner;
        }

        public void Resimulate(int receivedTick)
        {
            // if receivedTick = 100
            // then we want to Simulate (100->101)
            // so we pass tick 100 into Simulate

            // if lastSimTick = 105
            // then our last sim step will be (104->105)
            for (int tick = receivedTick; tick < lastSimTick; tick++)
            {
                behaviour.ApplyState(lastRecievedState);
                unappliedTick = false;

                // set forward appliying inputs
                // - exclude current tick, we will run this later
                if (tick - lastRecievedTick > _inputBuffer.Length)
                    throw new OverflowException("Inputs overflowed buffer");

                Simulate(tick);
            }
        }

        /// <summary>
        /// From tick N to N+1
        /// </summary>
        /// <param name="tick"></param>
        private void Simulate(int tick)
        {
            TInput input = GetInput(tick);
            TInput previous = GetInput(tick - 1);
            behaviour.ApplyInput(input, previous);
            behaviour.Simulate();
        }

        private int lastInputTick;

        public void InputTick(int tick)
        {
            if (lastInputTick != tick - 1)
                throw new Exception("Inputs ticks called out of order");
            lastInputTick = tick;

            TInput thisTickInput = behaviour.GetUnityInput();
            SetInput(tick, thisTickInput);
            behaviour.SendInput(tick, thisTickInput);
        }


        // todo add this tick delay stuff to tick runner rather than inside tick
        // maybe update tick/inputs at same interval as server
        // use tick rate stuff from snapshot interpolation

        // then use real time to send inputs to server??

        // maybe look at https://github.com/Unity-Technologies/FPSSample/blob/master/Assets/Scripts/Game/Main/ClientGameLoop.cs
        public void Tick(int inTick)
        {
            // delay from latency to make sure inputs reach server in time
            float tickDelay = getClientTick();

            Debug.Log($"{tickDelay:0.0}");

            int clientTick = inTick + (int)Math.Floor(tickDelay);
            while (clientTick > lastSimTick)
            {
                // set lastSim to +1, so if we receive new snapshot, then we sim up to 106 again
                // we only want to step forward 1 tick at a time so we collect inputs, and sim correctly
                // todo: what happens if we do 2 at once, is that really a problem?
                lastSimTick++;

                InputTick(lastSimTick);

                // we have inputs for 105, now we can simulate 106
                Simulate(lastSimTick);
            }
        }

        private float getClientTick()
        {
            // +2 to make sure inputs always get to server before simulation
            return 2 + calcualteTickDifference();
        }
        private float calcualteTickDifference()
        {
            double oneWayTrip = networkTime.Rtt / 2;
            float tickTime = tickRunner.TickInterval;

            double tickDifference = oneWayTrip / tickTime;
            return (float)tickDifference;
            //return (int)Math.Floor(tickDifference);
        }
    }

    public class ServerController<TInput, TState> where TInput : IInputState
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerController");

        readonly IPredictionBehaviour<TInput, TState> behaviour;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;
        /// <summary>Clears Previous inputs for tick (eg tick =100 clears tick 99's inputs</summary>
        void ClearPreviousInput(int tick) => SetInput(tick - 1, default);

        int lastRecieved = -1;

        public ServerController(IPredictionBehaviour<TInput, TState> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;
            _inputBuffer = new TInput[bufferSize];
        }

        internal void OnReceiveInput(int tick, TInput[] newInputs)
        {
            if (logger.LogEnabled()) logger.Log($"received inputs for {tick}. length: {newInputs.Length}");
            int lastTick = tick;

            for (int i = 0; i < newInputs.Length; i++)
            {
                int t = lastTick - i;
                TInput input = newInputs[i];
                // if new
                if (t > lastRecieved)
                {
                    SetInput(t, input);
                }
            }

            lastRecieved = Mathf.Max(lastRecieved, lastTick);
        }


        public void Tick(int tick)
        {
            TInput input = GetInput(tick);
            if (input.Valid)
            {
                TInput previous = GetInput(tick - 1);

                behaviour.ApplyInput(input, previous);
            }
            else
            {
                Debug.LogWarning($"No inputs for {tick}");
            }

            behaviour.Simulate();
            behaviour.SendState(tick, behaviour.GatherState());

            ClearPreviousInput(tick);
        }
    }

    public interface IInputState
    {
        bool Valid { get; }
    }
}
