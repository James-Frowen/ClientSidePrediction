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
using System.Threading.Tasks;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.CSP.Example2
{
    public class PredictionExample2 : PredictionBehaviour<InputState, ObjectState>, IDebugPredictionBehaviour
    {
        public float ResimulateLerp = 0.1f;
        [SerializeField] float speed = 15;

        static readonly ILogger logger = LogFactory.GetLogger<PredictionExample2>();

        private Rigidbody body;

        protected void Awake()
        {
            body = GetComponent<Rigidbody>();
        }

        public override void ApplyInputs(InputState input, InputState previous)
        {
            // normalised so that speed isn't faster if moving diagonal
            Vector3 move = new Vector3(x: input.Horizontal, y: 0, z: input.Vertical).normalized;

            Vector3 topOfCube = transform.position + Vector3.up * .5f;
            body.AddForceAtPosition(speed * move, topOfCube, ForceMode.Acceleration);
        }

        public override void NetworkFixedUpdate()
        {
            // no extra physics, rigidbody will apply its own gravity
        }

        public override void ApplyState(ObjectState state)
        {
            body.position = state.position;
            body.rotation = state.rotation;
            body.velocity = state.velocity;
            body.angularVelocity = state.angularVelocity;
        }
        public override void ResimulationTransition(ObjectState before, ObjectState after)
        {
            float t = ResimulateLerp;
            ObjectState state = default;
            state.position = Vector3.Lerp(before.position, after.position, t);
            state.rotation = Quaternion.Slerp(before.rotation, after.rotation, t);
            state.velocity = Vector3.Lerp(before.velocity, after.velocity, t);
            state.angularVelocity = Vector3.Lerp(before.angularVelocity, after.angularVelocity, t);
            ApplyState(state);
        }

        public override ObjectState GatherState()
        {
            return new ObjectState(body);
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
                horizontal: (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0),
                vertical: (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0)
            );
        }

        #region Move to weaver
        protected override void RegisterInputMessage(NetworkServer server, Action<INetworkPlayer, int, InputState[]> handler)
        {
            handlers.Add(NetId, handler);
            server.MessageHandler.RegisterHandler<InputMessage>(InputMessageHandler);
        }
        static Dictionary<uint, Action<INetworkPlayer, int, InputState[]>> handlers = new Dictionary<uint, Action<INetworkPlayer, int, InputState[]>>();
        static void InputMessageHandler(INetworkPlayer player, InputMessage msg)
        {
            handlers[msg.netId].Invoke(player, msg.tick, msg.inputs);
        }
        public override void PackInputMessage(NetworkWriter writer, int tick, InputState[] inputs)
        {
            var msg = new InputMessage
            {
                netId = NetId,
                tick = tick,
                inputs = inputs,
            };
            MessagePacker.Pack(msg, writer);
        }
        #endregion


        #region IDebugPredictionBehaviour
        bool _afterImage;
        PredictionExample2 _copy;
        IDebugPredictionBehaviour IDebugPredictionBehaviour.Copy { get => _copy; set => _copy = (PredictionExample2)value; }

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

        static Transform AfterImageParent;
        void IDebugPredictionBehaviour.CreateAfterImage(object _state)
        {
            if (!_afterImage) return;
            if (AfterImageParent == null)
                AfterImageParent = new GameObject("AfterImage").transform;

            var state = (ObjectState)_state;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.parent = AfterImageParent;
            Material mat = GetComponent<Renderer>().sharedMaterial;
            Renderer renderer = cube.GetComponent<Renderer>();
            renderer.material = Instantiate(mat);
            _ = changeColorOverTime(cube, renderer.material);
            cube.transform.SetPositionAndRotation(state.position, state.rotation);
        }

        private async Task changeColorOverTime(GameObject cube, Material material)
        {
            var a = new Color(1f, .4f, 0, 0.4f);
            var b = new Color(1f, .4f, 0, 0.0f);

            float start = Time.time;
            float end = start + 1;
            while (end > Time.time)
            {
                float t = (end - Time.time);
                // starts at t=1, so a is end point
                var color = Color.Lerp(b, a, t * t);
                material.color = color;
                await Task.Yield();
            }

            Destroy(material);
            Destroy(cube);
        }
        #endregion
    }

    [NetworkMessage]
    public struct InputMessage
    {
        public uint netId;
        public int tick;
        public InputState[] inputs;
    }

    [NetworkMessage]
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

    [NetworkMessage]
    public struct ObjectState
    {
        public bool Valid;
        public Vector3 position;
        [QuaternionPack(10)]
        public Quaternion rotation;
        public Vector3 velocity;
        public Vector3 angularVelocity;

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
