/*******************************************************
 * Copyright (C) 2010-2011 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using Mirage;
using UnityEngine;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// An Example of Client side prediction
    /// <para>NOT PREDUCTION READY</para>
    /// </summary>
    public class PredictionBehaviour : NetworkBehaviour
    {
        // 256 is probably too bug, but is fine for example
        const int BufferSize = 256;

        private TickRunner tickRunner;
        private Rigidbody body;


        private PhysicsScene physics;

        public void Simulate()
        {
            // stronger gravity when moving down
            if (body.velocity.y < 0) body.AddForce(2 * Physics.gravity, ForceMode.Acceleration);

            physics.Simulate(tickRunner.TickInterval);
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
                tickRunner.onTick += _server.Tick;
                physics = gameObject.scene.GetPhysicsScene();
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
            body.position = state.position;
            body.velocity = state.velocity;
        }

        private ObjectState GatherState()
        {
            return new ObjectState { position = body.position, velocity = body.velocity };
        }

        private void ApplyInput(InputState input, InputState previous)
        {
            const float speed = 15;

            var move = new Vector3(input.Horizonal, .25f /*small up force so it can move along floor*/, 0);
            body.AddForce(speed * move, ForceMode.Acceleration);
            if (input.jump && !previous.jump)
            {
                body.AddForce(Vector3.up * 10, ForceMode.Impulse);
            }
        }

        [ServerRpc]
        public void SendInput(int tick, InputState state)
        {
            Debug.Log($"recieved INPUT for {tick}");
            _server.SetInput(tick, state);
            _server.lastRecievedInput = tick;
        }
        [ClientRpc]
        public void SendState(int tick, ObjectState state)
        {
            Debug.Log($"recieved STATE for {tick}");
            _client.unappliedTick = true;
            _client.lastRecievedTick = tick;
            _client.lastRecievedState = state;
        }

        static int TickToBuffer(int tick)
        {
            //negative
            if (tick < 0)
                tick += BufferSize;
            return tick % BufferSize;
        }

        class ClientController
        {
            readonly PredictionBehaviour behaviour;

            InputState[] _inputBuffer;
            InputState GetInput(int tick) => _inputBuffer[TickToBuffer(tick)];
            void SetInput(int tick, InputState state) => _inputBuffer[TickToBuffer(tick)] = state;

            public bool unappliedTick;
            public int lastRecievedTick;
            public ObjectState lastRecievedState;


            public ClientController(PredictionBehaviour behaviour)
            {
                this.behaviour = behaviour;
                _inputBuffer = new InputState[BufferSize];
            }

            public void Tick(int tick)
            {
                SetInput(tick, GetUnityInput());

                // if new data from server
                if (unappliedTick)
                {
                    // set state to server's state
                    behaviour.ApplyState(lastRecievedState);

                    // set forward appliying inputs
                    // - exclude current tick, we will run this later
                    for (int t = lastRecievedTick; t < tick; t++)
                    {
                        Step(t);
                    }

                    unappliedTick = false;
                }

                // run current tick+inputs
                Step(tick);

                behaviour.SendInput(tick, GetInput(tick));
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
                return new InputState
                {
                    right = Input.GetKey(KeyCode.D),
                    left = Input.GetKey(KeyCode.A),
                    jump = Input.GetKey(KeyCode.Space),
                };
            }
        }
        class ServerController
        {
            readonly PredictionBehaviour behaviour;

            InputState[] _inputBuffer;
            InputState GetInput(int tick) => _inputBuffer[TickToBuffer(tick)];
            public void SetInput(int tick, InputState state) => _inputBuffer[TickToBuffer(tick)] = state;

            ObjectState[] _objectBuffer;
            ObjectState GetState(int tick) => _objectBuffer[TickToBuffer(tick)];
            void SetState(int tick, ObjectState state) => _objectBuffer[TickToBuffer(tick)] = state;

            public int lastRecievedInput = -1;
            int lastAppliedInput = -1;

            public ServerController(PredictionBehaviour behaviour)
            {
                this.behaviour = behaviour;
                _inputBuffer = new InputState[BufferSize];
                _objectBuffer = new ObjectState[BufferSize];
            }

            public void Tick(int tick)
            {
                //special case for first input
                if (lastRecievedInput == -1) return;
                if (lastAppliedInput == -1)
                {
                    lastAppliedInput = lastRecievedInput - 1;
                }

                if (lastAppliedInput != lastRecievedInput)
                {
                    // apply last state with inputs
                    behaviour.ApplyState(GetState(lastAppliedInput));

                    // apply all missing inputs
                    for (int t = lastAppliedInput + 1; t <= lastRecievedInput; t++)
                    {
                        // apply inputs up to received
                        InputState input = GetInput(t);
                        InputState previous = GetInput(t - 1);
                        behaviour.ApplyInput(input, previous);
                        behaviour.Simulate();

                        SetState(t, behaviour.GatherState());
                    }
                    lastAppliedInput = lastRecievedInput;
                }

                // simulate up to current tick
                for (int t = lastRecievedInput + 1; t <= tick; t++)
                {
                    behaviour.Simulate();

                    SetState(t, behaviour.GatherState());
                }

                behaviour.SendState(tick, GetState(tick));
            }
        }
    }

    public struct InputState
    {
        public bool jump;
        public bool left;
        public bool right;

        public int Horizonal => (right ? 1 : 0) - (left ? 1 : 0);
    }

    public struct ObjectState
    {
        public Vector3 position;
        public Vector3 velocity;
    }
}
