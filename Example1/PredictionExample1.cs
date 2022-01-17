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

namespace JamesFrowen.CSP.Example1
{
    public class PredictionExample1 : PredictionBehaviour<InputState, ObjectState>, IDebugPredictionBehaviour
    {
        static readonly ILogger logger = LogFactory.GetLogger<PredictionExample1>();

        private Rigidbody body;
        const float speed = 15;

        protected void Awake()
        {
            body = GetComponent<Rigidbody>();
        }

        public override void ApplyInputs(InputState input, InputState previous)
        {
            Vector3 move = input.Horizontal * new Vector3(1, .25f /*small up force so it can move along floor*/, 0);
            body.AddForce(speed * move, ForceMode.Acceleration);
            if (input.jump && !previous.jump)
            {
                body.AddForce(Vector3.up * 10, ForceMode.Impulse);
            }
        }
        public override void NetworkFixedUpdate()
        {
            // stronger gravity when moving down
            float gravity = body.velocity.y < 0 ? 3 : 1;
            body.AddForce(gravity * Physics.gravity, ForceMode.Acceleration);
            body.velocity += (gravity * Physics.gravity) * PredictionTime.FixedDeltaTime;
        }

        public override void ApplyState(ObjectState state)
        {
            body.position = state.position;
            body.velocity = state.velocity;
            body.rotation = Quaternion.identity;
            body.angularVelocity = Vector3.zero;
        }
        public override void ResimulationTransition(ObjectState current, ObjectState next)
        {
            // no smoothing
        }

        public override ObjectState GatherState()
        {
            return new ObjectState(body.position, body.velocity);
        }

        public override bool HasInput => true;

        public override InputState MissingInput(InputState previous, int previousTick, int currentTick)
        {
            // just copy old input, It is likely that missing input is just same as previous
            return previous;
        }

        public override InputState GetInput()
        {
            return new InputState(
                right: Input.GetKey(KeyCode.D),
                left: Input.GetKey(KeyCode.A),
                jump: Input.GetKey(KeyCode.Space)
            );
        }

        #region Move to weaver
        protected override void RegisterInputMessage(NetworkServer server, Action<INetworkPlayer, int, InputState[]> handler)
        {
            server.MessageHandler.RegisterHandler<InputMessage>((player, msg) => handler.Invoke(player, msg.tick, msg.inputs));
        }
        public override void PackInputMessage(NetworkWriter writer, int tick, InputState[] inputs)
        {
            var msg = new InputMessage
            {
                tick = tick,
                inputs = inputs,
            };
            MessagePacker.Pack(msg, writer);
        }
        #endregion


        #region IDebugPredictionBehaviour
        PredictionExample1 _copy;
        IDebugPredictionBehaviour IDebugPredictionBehaviour.Copy { get => _copy; set => _copy = (PredictionExample1)value; }

        void IDebugPredictionBehaviour.Setup(IPredictionTime time)
        {
            PredictionTime = time;
        }

        InputState noNetworkPrevious;
        void IDebugPredictionBehaviour.NoNetworkApply(object _input)
        {
            var input = (InputState)_input;
            ApplyInputs(input, noNetworkPrevious);
            NetworkFixedUpdate();
            gameObject.scene.GetPhysicsScene().Simulate(PredictionTime.FixedDeltaTime);
            noNetworkPrevious = input;
        }
        void IDebugPredictionBehaviour.CreateAfterImage(object state) { }
        #endregion
    }

    [NetworkMessage]
    public struct InputMessage
    {
        public int tick;
        public InputState[] inputs;
    }

    [NetworkMessage]
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

        public int Horizontal => (right ? 1 : 0) - (left ? 1 : 0);
    }

    [NetworkMessage]
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
