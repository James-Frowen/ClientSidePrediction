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
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.CSP.Example2
{
    public class PredictionExample2 : PredictionBehaviour<InputState, ObjectState>, IDebugPredictionBehaviour
    {
        [SerializeField] float speed = 15;

        static readonly ILogger logger = LogFactory.GetLogger<PredictionExample2>();

        private Rigidbody body;

        protected override void Awake()
        {
            body = GetComponent<Rigidbody>();

            base.Awake();
        }

        public override void NetworkFixedUpdate(float fixedDelta)
        {
            // stronger gravity when moving down
            float gravity = body.velocity.y < 0 ? 3 : 1;
            body.AddForce(gravity * Physics.gravity, ForceMode.Acceleration);
            body.velocity += (gravity * Physics.gravity) * fixedDelta;
        }

        public override void ApplyState(ObjectState state)
        {
            body.position = state.position;
            body.rotation = state.rotation;
            body.velocity = state.velocity;
            body.angularVelocity = state.angularVelocity;
        }

        public override ObjectState GatherState()
        {
            return new ObjectState(body);
        }

        public override void ApplyInput(InputState input, InputState previous)
        {
            // normalised so that speed isn't faster if moving diagonal
            Vector3 move = new Vector3(x: input.Horizontal, y: 0, z: input.Vertical).normalized;

            Vector3 topOfCube = transform.position + Vector3.up * .5f;
            body.AddForceAtPosition(speed * move, topOfCube, ForceMode.Acceleration);
        }

        public override InputState MissingInput(InputState previous, int previousTick, int currentTick)
        {
            // just copy old input, It is likely that missing input is just same as previous
            return previous;
        }

        public override InputState GetInput()
        {
            return new InputState(
                horizontal: (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0),
                vertical: (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0)
            );
        }

        #region Move to weaver
        protected override void RegisterInputMessage(NetworkServer server, Action<int, InputState[]> handler)
        {
            server.MessageHandler.RegisterHandler<InputMessage>(x => handler.Invoke(x.tick, x.inputs));
        }
        protected override void PackInputMessage(NetworkWriter writer, int tick, InputState[] inputs)
        {
            var msg = new InputMessage
            {
                tick = tick,
                inputs = inputs,
            };
            MessagePacker.Pack(msg, writer);
        }
        public override void SendState(int tick, ObjectState state)
        {
            SendState_RPC(tick, state);
        }
        [ClientRpc(channel = Channel.Unreliable)]
        private void SendState_RPC(int tick, ObjectState state)
        {
            SendState_Receive(tick, state);
        }
        #endregion


        #region IDebugPredictionBehaviour
        PredictionExample2 _copy;
        TickRunner _DebugRunner;
        IDebugPredictionBehaviour IDebugPredictionBehaviour.Copy { get => _copy; set => _copy = (PredictionExample2)value; }

        void IDebugPredictionBehaviour.Setup(TickRunner runner)
        {
            _DebugRunner = runner;
        }

        InputState noNetworkPrevious;
        void IDebugPredictionBehaviour.NoNetworkApply(object _input)
        {
            var input = (InputState)_input;
            ApplyInput(input, noNetworkPrevious);
            NetworkFixedUpdate(_DebugRunner.TickInterval);
            gameObject.scene.GetPhysicsScene().Simulate(_DebugRunner.TickInterval);
            noNetworkPrevious = input;
        }
        #endregion
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

        [BitCountFromRange(-1, 1)] public readonly int Horizontal;
        [BitCountFromRange(-1, 1)] public readonly int Vertical;

        public InputState(int horizontal, int vertical)
        {
            Horizontal = horizontal;
            Vertical = vertical;
            _valid = true;
        }
    }

    public struct ObjectState
    {
        public readonly bool Valid;
        public readonly Vector3 position;
        [QuaternionPack(10)] public readonly Quaternion rotation;
        public readonly Vector3 velocity;
        public readonly Vector3 angularVelocity;

        public ObjectState(Rigidbody body)
        {
            position = body.position;
            rotation = body.rotation;
            velocity = body.velocity;
            angularVelocity = body.angularVelocity;
            Valid = true;
        }
    }
}