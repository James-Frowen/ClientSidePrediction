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
    /// <summary>
    /// An Example of Client side prediction
    /// <para>NOT PREDUCTION READY</para>
    /// </summary>
    public class PredictionBehaviour : NetworkBehaviour
    {
        const int NO_VALUE = -1;
        static readonly ILogger logger = LogFactory.GetLogger<PredictionBehaviour>();

        // 256 is probably too bug, but is fine for example
        const int BufferSize = 256;

        public TickRunner tickRunner;
        private Rigidbody body;

        public PredictionBehaviour Copy;

        public PhysicsScene physics;

        public void Simulate()
        {
            // stronger gravity when moving down
            float gravity = body.velocity.y < 0 ? 3 : 1;
            body.AddForce(gravity * Physics.gravity, ForceMode.Acceleration);
            body.velocity += (gravity * Physics.gravity) * tickRunner.TickInterval;

            physics.Simulate(tickRunner.TickInterval);
        }

        InputState noNetworkPrevious;
        private void NoNetworkApply(InputState input)
        {
            ApplyInput(input, noNetworkPrevious);
            Simulate();
            noNetworkPrevious = input;
        }

        ClientController _client;
        ServerController _server;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();

            Identity.OnStartServer.AddListener(() =>
            {
                _server = new ServerController(this);
                tickRunner = (Server as MonoBehaviour).GetComponent<TickRunner>();
                // todo remove tick after destroyed

                if (tickRunner.slowMode)
                {
                    tickRunner.Server = _server;
                }
                else
                {
                    tickRunner.onTick += _server.Tick;
                }
                physics = gameObject.scene.GetPhysicsScene();

                // todo why doesn't IServer have message handler
                ((NetworkServer)Identity.Server).MessageHandler.RegisterHandler<InputMessage>(HandleInputMessage);
            });
            Identity.OnStartClient.AddListener(() =>
            {
                _client = new ClientController(this);
                tickRunner = (Client as MonoBehaviour).GetComponent<TickRunner>();
                // todo remove tick after destroyed
                tickRunner.onTick += _client.Tick;
                physics = gameObject.scene.GetPhysicsScene();
            });
        }

        private void ApplyState(ObjectState state)
        {
            state.Validate();
            body.position = state.position;
            body.velocity = state.velocity;
            body.rotation = Quaternion.identity;
            body.angularVelocity = Vector3.zero;
        }

        private ObjectState GatherState()
        {
            return new ObjectState(body.position, body.velocity);
        }

        private void ApplyInput(InputState input, InputState previous)
        {
            input.Validate();
            previous.Validate();
            const float speed = 15;

            Vector3 move = input.Horizonal * new Vector3(1, .25f /*small up force so it can move along floor*/, 0);
            body.AddForce(speed * move, ForceMode.Acceleration);
            if (input.jump && !previous.jump)
            {
                body.AddForce(Vector3.up * 10, ForceMode.Impulse);
            }
        }

        Dictionary<int, InputState> pendingInputs = new Dictionary<int, InputState>();
        //Dictionary<int, int> sendInputs = new Dictionary<int, int>();

        [Client]
        public void SendInput(int tick, InputState state)
        {
            pendingInputs.Add(tick, state);

            var msg = new InputMessage
            {
                tick = tick,
                inputs = new InputState[pendingInputs.Count],
            };
            foreach (KeyValuePair<int, InputState> pending in pendingInputs)
            {
                msg.inputs[tick - pending.Key] = pending.Value;
            }
            //sendInputs[tick] = msg.inputs.Length;


            if (tickRunner.slowMode)
            {
                inputSendHolder.Enqueue((tick, tick + tickRunner.fakeDelay, msg));
                while (inputSendHolder.Count > 0 && inputSendHolder.Peek().receive <= tick)
                {
                    receiveInput();
                }
            }
            else
            {
                IConnection conn = Client.Player.Connection;

                using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
                {
                    MessagePacker.Pack(msg, writer);
                    INotifyToken token = conn.SendNotify(writer.ToArraySegment());
                    token.Delivered += () =>
                    {
                        for (int i = 0; i < msg.inputs.Length; i++)
                        {
                            // once message is acked, remove all inputs starting at the tick
                            pendingInputs.Remove(tick - i);
                        }
                    };
                }
            }
        }

        private void receiveInput()
        {
            (int sent, int receive, InputMessage received) pack = inputSendHolder.Dequeue();
            HandleInputMessage(null, pack.received);
            for (int i = 0; i < pack.received.inputs.Length; i++)
            {
                // once message is acked, remove all inputs starting at the tick
                pendingInputs.Remove(pack.sent - i);
            }
        }

        Queue<(int sent, int receive, InputMessage received)> inputSendHolder = new Queue<(int, int, InputMessage)>();
        Queue<(int sent, int receive, ObjectState received)> stateSendHolder = new Queue<(int, int, ObjectState)>();
        void HandleInputMessage(INetworkPlayer _sender, InputMessage message)
        {
            Debug.Log($"recieved INPUT for {message.tick}");
            tickRunner.Server.OnReceiveInput(message);
        }

        [ClientRpc(channel = Channel.Unreliable)]
        public void SendState(int tick, ObjectState state)
        {
            if (tickRunner.slowMode)
            {
                stateSendHolder.Enqueue((tick, tick + tickRunner.fakeDelay, state));
                while (stateSendHolder.Count > 0 && stateSendHolder.Peek().receive <= tick)
                {
                    (int sent, int receive, ObjectState received) pack = stateSendHolder.Dequeue();
                    _client.ReceiveState(pack.sent, pack.received);
                }
            }
            else
            {
                _client.ReceiveState(tick, state);
            }
        }

        static int TickToBuffer(int tick)
        {
            //negative
            if (tick < 0)
                tick += BufferSize;
            return tick % BufferSize;
        }

        public struct InputMessage
        {
            public int tick;
            public InputState[] inputs;
        }

        class ClientController
        {
            readonly PredictionBehaviour behaviour;

            InputState[] _inputBuffer;
            InputState GetInput(int tick) => _inputBuffer[TickToBuffer(tick)];
            void SetInput(int tick, InputState state) => _inputBuffer[TickToBuffer(tick)] = state;

            public bool unappliedTick;
            public int lastRecievedTick = NO_VALUE;
            public ObjectState lastRecievedState;


            public void ReceiveState(int tick, ObjectState state)
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
            }


            public ClientController(PredictionBehaviour behaviour)
            {
                this.behaviour = behaviour;
                _inputBuffer = new InputState[BufferSize];
            }


            public void Tick(int tick)
            {
                int tickDelay = CalcualteTickDifference();

                // todo add this tick delay stuff to tick runner rather than inside tick
                throw new System.NotImplementedException();

                InputState thisTickInput = GetUnityInput();
                SetInput(tick, thisTickInput);
                behaviour.Copy.NoNetworkApply(thisTickInput);
                behaviour.SendInput(tick, thisTickInput);

                if (unappliedTick)
                    Resim(tick - 1);

                // step this tick
                Step(tick);
            }


            private int CalcualteTickDifference()
            {
                double oneWayTrip = behaviour.NetworkTime.Rtt / 2;
                float tickTime = behaviour.tickRunner.TickInterval;

                double tickDifference = oneWayTrip * tickTime;
                // +2 to make sure inputs always get to server before simulation
                return 2 + (int)Math.Floor(tickDifference);
            }

            private void Resim(int tick)
            {
                // set state to server's state
                behaviour.ApplyState(lastRecievedState);

                // set forward appliying inputs
                // - exclude current tick, we will run this later
                if (tick - lastRecievedTick > BufferSize)
                    throw new OverflowException("Inputs overflowed buffer");

                // todo is this number right
                for (int t = lastRecievedTick; t < tick; t++)
                {
                    Step(t);
                }

                unappliedTick = false;
            }

            private void Step(int t)
            {
                InputState input = GetInput(t);
                InputState previous = GetInput(t - 1);
                behaviour.ApplyInput(input, previous);
                behaviour.Simulate();
            }

            InputState GetUnityInput()
            {
                return new InputState(
                    right: Input.GetKey(KeyCode.D),
                    left: Input.GetKey(KeyCode.A),
                    jump: Input.GetKey(KeyCode.Space)
                );
            }
        }

        public class ServerController
        {
            readonly PredictionBehaviour behaviour;

            InputState[] _inputBuffer;
            InputState GetInput(int tick) => _inputBuffer[TickToBuffer(tick)];
            void SetInput(int tick, InputState state) => _inputBuffer[TickToBuffer(tick)] = state;
            /// <summary>Clears Previous inputs for tick (eg tick =100 clears tick 99's inputs</summary>
            void ClearPreviousInput(int tick) => SetInput(tick - 1, default);

            int lastRecieved = -1;

            public ServerController(PredictionBehaviour behaviour)
            {
                this.behaviour = behaviour;
                _inputBuffer = new InputState[BufferSize];
            }

            internal void OnReceiveInput(InputMessage msg)
            {
                int lastTick = msg.tick;

                for (int i = 0; i < msg.inputs.Length; i++)
                {
                    int t = lastTick - i;
                    InputState input = msg.inputs[i];
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
                InputState input = GetInput(tick);
                if (input.Valid)
                {
                    InputState previous = GetInput(tick - 1);

                    behaviour.ApplyInput(input, previous);
                }

                behaviour.Simulate();
                behaviour.SendState(tick, behaviour.GatherState());

                ClearPreviousInput(tick);
            }
        }

        public struct InputState
        {
            public readonly bool Valid;
            public readonly bool jump;
            public readonly bool left;
            public readonly bool right;

            public InputState(bool right, bool left, bool jump)
            {
                this.jump = jump;
                this.left = left;
                this.right = right;
                Valid = true;
            }

            public int Horizonal => (right ? 1 : 0) - (left ? 1 : 0);

            public void Validate()
            {
                if (!Valid) { }
                //Debug.LogError("Input Invalid");
                throw new Exception("Input is not valid");
            }
        }

        public struct ObjectState
        {
            public readonly bool Valid;
            public readonly Vector3 position;
            public readonly Vector3 velocity;

            public ObjectState(Vector3 position, Vector3 velocity)
            {
                this.position = position;
                this.velocity = velocity;
                Valid = true;
            }

            public void Validate()
            {
                if (!Valid) { }
                //Debug.LogError("State Invalid");
                throw new Exception("State is not valid");
            }
        }
    }
}
