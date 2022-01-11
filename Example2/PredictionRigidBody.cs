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
    public class PredictionRigidBody : PredictionBehaviour<NoInput, ObjectState>
    {
        static readonly ILogger logger = LogFactory.GetLogger<PredictionExample2>();

        public float ResimulateLerp = 0.1f;
        private Rigidbody body;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
        }

        public override bool HasInput => false;
        public override NoInput GetInput() => default;
        public override NoInput MissingInput(NoInput previous, int previousTick, int currentTick) => default;
        public override void ApplyInput(NoInput input, NoInput previous) { }
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

        public override void NetworkFixedUpdate(float fixedDelta) { }

        public override void PackInputMessage(NetworkWriter writer, int tick, NoInput[] inputs) { }
        protected override void RegisterInputMessage(NetworkServer server, Action<int, NoInput[]> handler) { }
    }
    [NetworkMessage]
    public struct NoInput : IInputState
    {
        public bool Valid => true;
    }
}