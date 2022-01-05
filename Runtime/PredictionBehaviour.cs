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
    public abstract class PredictionBehaviour<TInput, TState> : NetworkBehaviour where TInput : IInputState
    {
        TickRunner tickRunner;
        ClientController<TInput, TState> _client;
        ServerController<TInput, TState> _server;

        Dictionary<int, TInput> pendingInputs = new Dictionary<int, TInput>();

        public abstract TInput GetInput();
        public abstract TInput MissingInput(TInput previous, int previousTick, int currentTick);
        public abstract void ApplyInput(TInput input, TInput previous);
        public abstract void ApplyState(TState state);
        public abstract TState GatherState();
        public abstract void NetworkFixedUpdate(float fixedDelta);

        // todo generate by weaver
        protected abstract void RegisterInputMessage(NetworkServer server, Action<int, TInput[]> handler);
        protected abstract void PackInputMessage(NetworkWriter writer, int tick, TInput[] inputs);

        protected virtual void Awake()
        {
            Identity.OnStartServer.AddListener(() =>
            {
                tickRunner = (Server as MonoBehaviour).GetComponent<TickRunner>();
                _server = new ServerController<TInput, TState>(this, tickRunner, Helper.BufferSize);
                // todo remove tick after destroyed

                if (tickRunner.slowMode)
                {
                    throw new NotImplementedException("Slow mode not supported");
                    //tickRunner.Server = _server;
                }
                else
                {
                    tickRunner.onTick += _server.Tick;
                }

                // todo why doesn't IServer have message handler
                var networkServer = ((NetworkServer)Identity.Server);
                RegisterInputMessage(networkServer, (tick, inputs) => _server.OnReceiveInput(tick, inputs));
            });
            Identity.OnStartClient.AddListener(() =>
            {
                tickRunner = (Client as MonoBehaviour).GetComponent<TickRunner>();
                _client = new ClientController<TInput, TState>(this, NetworkTime, tickRunner, Helper.BufferSize);
                // todo remove tick after destroyed
                tickRunner.onTick += _client.Tick;
            });
        }


        // for slow mode
        //Queue<(int sent, int receive, InputMessage received)> inputSendHolder = new Queue<(int, int, InputMessage)>();
        //Queue<(int sent, int receive, TState received)> stateSendHolder = new Queue<(int, int, TState)>();

        [Client]
        public void SendInput(int tick, TInput state)
        {
            if (this is IDebugPredictionBehaviour debug)
                debug.Copy.NoNetworkApply(state);

            pendingInputs.Add(tick, state);

            var inputs = new TInput[pendingInputs.Count];
            foreach (KeyValuePair<int, TInput> pending in pendingInputs)
            {
                inputs[tick - pending.Key] = pending.Value;
            }

            if (tickRunner.slowMode)
            {
                throw new NotImplementedException("Slow mode not supported");
                //inputSendHolder.Enqueue((tick, tick + tickRunner.fakeDelay, msg));
                //while (inputSendHolder.Count > 0 && inputSendHolder.Peek().receive <= tick)
                //{
                //    receiveInput();
                //}

                //void receiveInput()
                //{
                //    (int sent, int receive, InputMessage received) pack = inputSendHolder.Dequeue();
                //    HandleInputMessage(null, pack.received);
                //    for (int i = 0; i < pack.received.inputs.Length; i++)
                //    {
                //        // once message is acked, remove all inputs starting at the tick
                //        pendingInputs.Remove(pack.sent - i);
                //    }
                //}
            }
            else
            {
                IConnection conn = Client.Player.Connection;

                using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
                {
                    PackInputMessage(writer, tick, inputs);

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

        public abstract void SendState(int tick, TState state);
        protected void SendState_Receive(int tick, TState state)
        {
            if (tickRunner.slowMode)
            {
                throw new NotImplementedException("Slow mode not supported");
                //stateSendHolder.Enqueue((tick, tick + tickRunner.fakeDelay, state));
                //while (stateSendHolder.Count > 0 && stateSendHolder.Peek().receive <= tick)
                //{
                //    (int sent, int receive, TState received) pack = stateSendHolder.Dequeue();
                //    _client.ReceiveState(pack.sent, pack.received);
                //}
            }
            else
            {
                _client.ReceiveState(tick, state);
            }
        }
    }

    public interface IDebugPredictionBehaviour
    {
        IDebugPredictionBehaviour Copy { get; set; }

        void NoNetworkApply(object input);
        void Setup(TickRunner runner);
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

    public class ClientController<TInput, TState> where TInput : IInputState
    {
        static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientController");

        readonly PredictionBehaviour<TInput, TState> behaviour;
        readonly NetworkTime networkTime;
        readonly TickRunner tickRunner;
        readonly PhysicsScene physics;

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


        public ClientController(PredictionBehaviour<TInput, TState> behaviour, NetworkTime networkTime, TickRunner tickRunner, int bufferSize)
        {
            this.behaviour = behaviour;
            physics = behaviour.gameObject.scene.GetPhysicsScene();
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
            behaviour.NetworkFixedUpdate(tickRunner.TickInterval);
            physics.Simulate(tickRunner.TickInterval);
        }

        private int lastInputTick;

        public void InputTick(int tick)
        {
            if (lastInputTick != tick - 1)
                throw new Exception("Inputs ticks called out of order");
            lastInputTick = tick;

            TInput thisTickInput = behaviour.GetInput();
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

        readonly PredictionBehaviour<TInput, TState> behaviour;
        readonly TickRunner tickRunner;
        readonly PhysicsScene physics;

        TInput[] _inputBuffer;
        TInput GetInput(int tick) => _inputBuffer[Helper.TickToBuffer(tick)];
        void SetInput(int tick, TInput state) => _inputBuffer[Helper.TickToBuffer(tick)] = state;
        /// <summary>Clears Previous inputs for tick (eg tick =100 clears tick 99's inputs</summary>
        void ClearPreviousInput(int tick) => SetInput(tick - 1, default);

        int lastRecieved = -1;

        public ServerController(PredictionBehaviour<TInput, TState> behaviour, TickRunner tickRunner, int bufferSize)
        {
            this.behaviour = behaviour;
            this.tickRunner = tickRunner;
            physics = behaviour.gameObject.scene.GetPhysicsScene();
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

            behaviour.NetworkFixedUpdate(tickRunner.TickInterval);
            physics.Simulate(tickRunner.TickInterval);
            behaviour.SendState(tick, behaviour.GatherState());

            ClearPreviousInput(tick);
        }
    }

    public interface IInputState
    {
        bool Valid { get; }
    }
}
