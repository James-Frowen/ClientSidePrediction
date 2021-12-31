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
namespace JamesFrowen.CSP.Example1
{
    public class PredictionExample1 : NetworkBehaviour, IPredictionBehaviour<InputState, ObjectState>, IDebugPredictionBehaviour
    {
        static readonly ILogger logger = LogFactory.GetLogger<PredictionExample1>();

        public TickRunner tickRunner;
        private Rigidbody body;

        PredictionExample1 _copy;
        IDebugPredictionBehaviour IDebugPredictionBehaviour.Copy { get => _copy; set => _copy = (PredictionExample1)value; }

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

        ClientController<InputState, ObjectState> _client;
        ServerController<InputState, ObjectState> _server;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();

            Identity.OnStartServer.AddListener(() =>
            {
                _server = new ServerController<InputState, ObjectState>(this, Helper.BufferSize);
                tickRunner = (Server as MonoBehaviour).GetComponent<TickRunner>();
                // todo remove tick after destroyed

                if (tickRunner.slowMode)
                {
                    throw new NotImplementedException();
                    //tickRunner.Server = _server;
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
                tickRunner = (Client as MonoBehaviour).GetComponent<TickRunner>();
                _client = new ClientController<InputState, ObjectState>(this, NetworkTime, tickRunner, Helper.BufferSize);
                // todo remove tick after destroyed
                tickRunner.onTick += _client.Tick;
                physics = gameObject.scene.GetPhysicsScene();
            });
        }
        void IDebugPredictionBehaviour.Setup(TickRunner runner)
        {
            tickRunner = runner;
            physics = gameObject.scene.GetPhysicsScene();
        }

        public void ApplyState(ObjectState state)
        {
            body.position = state.position;
            body.velocity = state.velocity;
            body.rotation = Quaternion.identity;
            body.angularVelocity = Vector3.zero;
        }

        public ObjectState GatherState()
        {
            return new ObjectState(body.position, body.velocity);
        }

        public void ApplyInput(InputState input, InputState previous)
        {
            const float speed = 15;

            Vector3 move = input.Horizonal * new Vector3(1, .25f /*small up force so it can move along floor*/, 0);
            body.AddForce(speed * move, ForceMode.Acceleration);
            if (input.jump && !previous.jump)
            {
                body.AddForce(Vector3.up * 10, ForceMode.Impulse);
            }
        }

        //todo move to client controler
        Dictionary<int, InputState> pendingInputs = new Dictionary<int, InputState>();

        [Client]
        public void SendInput(int tick, InputState state)
        {
            _copy.NoNetworkApply(state);
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
            //Debug.Log($"recieved INPUT for {message.tick}");
            //var server = _sender == null ? tickRunner.Server : _server;
            _server.OnReceiveInput(message.tick, message.inputs);
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

        public InputState GetUnityInput()
        {
            return new InputState(
                right: Input.GetKey(KeyCode.D),
                left: Input.GetKey(KeyCode.A),
                jump: Input.GetKey(KeyCode.Space)
            );
        }

    }
    public struct InputMessage
    {
        public int tick;
        public InputState[] inputs;
    }

    public struct InputState : IInputState
    {
        public bool Valid => _valid;

        public readonly bool _valid;
        public readonly bool jump;
        public readonly bool left;
        public readonly bool right;

        public InputState(bool right, bool left, bool jump)
        {
            this.jump = jump;
            this.left = left;
            this.right = right;
            _valid = true;
        }

        public int Horizonal => (right ? 1 : 0) - (left ? 1 : 0);
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
    }
}
