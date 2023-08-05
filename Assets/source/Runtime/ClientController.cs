/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using JamesFrowen.DeltaSnapshot;
using Mirage.Logging;
using Mirage.Serialization;
using UnityEngine;
using UnityEngine.Assertions;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// Controls 1 behaviour on client only
    /// </summary>
    /// <typeparam name="TInput"></typeparam>
    internal unsafe class ClientController<TInput> : IClientController
    {
        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ClientController");
        private readonly PredictionBehaviour<TInput> behaviour;
        /// <summary>
        /// could be null if behaviour does not have any snapshots
        /// </summary>
        private ISnapshotBehaviourGenerated _snapshotBehaviour;

        /// <summary>
        /// could be null, only used if behaviour is also ISnapshotBehaviourGenerated
        /// </summary>
        private IResimulationCallbacks _resimulationCallbacks;
        private readonly NullableRingBuffer<TInput> _inputBuffer;

        private bool hasSimulatedLocally;
        private bool hasBeforeResimulateState;
        private int[] _beforeResimulateState;

        public ClientController(PredictionBehaviour<TInput> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;
            _snapshotBehaviour = behaviour as ISnapshotBehaviourGenerated;
            _beforeResimulateState = new int[_snapshotBehaviour.AllocationSizeInts];

            // these buffers are small 
            // dont worry about authority, just create one for all objects
            if (behaviour.HasInput)
                _inputBuffer = new NullableRingBuffer<TInput>(bufferSize);
        }

        public void BeforeResimulate()
        {
            // we only want to do store before re-simulatuion state if we have simulated any steps locally.
            // otherwise we just want to apply state from server
            if (hasSimulatedLocally && behaviour is IResimulationCallbacks)
            {
                UnsafeHelper.Copy(_snapshotBehaviour.Ptr, _beforeResimulateState, _beforeResimulateState.Length);
                hasBeforeResimulateState = true;
            }
        }

        public void AfterResimulate()
        {
            if (hasBeforeResimulateState && behaviour is IResimulationCallbacks callbacks)
            {
                fixed (int* before = &_beforeResimulateState[0])
                {
                    var snapshot = new ResimulationSnapshot()
                    {
                        Before = before,
                        NameToOffset = _snapshotBehaviour.SnapshotMetadata.NameToOffset
                    };

                    callbacks.ResimulationTransition(snapshot);
                }

                // todo do we need to set AfterStateChanged here, user could just set values inside ResimulationTransition instead
                //      they dont need to set state itself, Just the transform
                // todo can we fully remove AfterStateChanged. It is only used to set untiy state after changing snapshot values
                //      we might be able to just use LateUpdate instead
                behaviour.AfterStateChanged();

                if (behaviour is IDebugPredictionAfterImage debug && debug.ShowAfterImage)
                {
                    // just re-use this struct for debug, it just needs the current snapsot in order to display effect
                    var snapshots = new ResimulationSnapshot()
                    {
                        Before = _snapshotBehaviour.Ptr,
                        NameToOffset = _snapshotBehaviour.SnapshotMetadata.NameToOffset
                    };
                    debug.CreateAfterImage(snapshots, new Color(0, 0.4f, 1f));
                }

                _beforeResimulateState = default;
                hasBeforeResimulateState = false;
            }
        }

        /// <summary>
        /// From tick N to N+1
        /// </summary>
        /// <param name="tick"></param>
        void IClientController.Simulate(int tick)
        {
            if (behaviour.UseInputs())
            {
                var input = _inputBuffer.GetOrDefault(tick);
                var previous = _inputBuffer.GetOrDefault(tick - 1);
                behaviour.ApplyInputs(new NetworkInputs<TInput>(input, previous));
            }
            behaviour.NetworkFixedUpdate();
            hasSimulatedLocally = true;
        }

        public void InputTick(int tick)
        {
            Assert.IsTrue(behaviour.UseInputs());

            var thisTickInput = behaviour.GetInput();
            _inputBuffer.Set(tick, thisTickInput);

            if (behaviour is IDebugPredictionLocalCopy debug)
                debug.Copy?.NoNetworkApply(_inputBuffer.GetOrDefault(tick));
        }

        void IClientController.WriteInput(NetworkWriter writer, int tick)
        {
            var input = _inputBuffer.GetOrDefault(tick);
            writer.Write(input);
        }
    }
}
