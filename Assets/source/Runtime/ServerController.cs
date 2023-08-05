/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using JamesFrowen.DeltaSnapshot;
using Mirage.Logging;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// Controls 1 behaviour on server and host
    /// </summary>
    /// <typeparam name="TInput"></typeparam>
    internal class ServerController<TInput> : IServerController
    {
        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerController");
        private readonly PredictionBehaviour<TInput> behaviour;
        private readonly NullableRingBuffer<TInput> _inputBuffer;

        private (int tick, TInput input) lastValidInput;
        private int? lastReceived = null;
        private bool hostMode;

        void IServerController.SetHostMode()
        {
            hostMode = true;
        }

        public ServerController(PredictionBehaviour<TInput> behaviour, int bufferSize)
        {
            this.behaviour = behaviour;

            // these buffers are small 
            // dont worry about authority, just create one for all objects
            if (behaviour.HasInput)
                _inputBuffer = new NullableRingBuffer<TInput>(bufferSize);
        }

        void IServerController.ReadInput(PlayerTimeTracker tracker, NetworkReader reader, int inputTick, int lastSimulation)
        {
            var input = reader.Read<TInput>();
            // if new, and after last sim
            if (inputTick > tracker.lastReceivedInput && inputTick > lastSimulation)
            {
                lastReceived = tracker.lastReceivedInput;
                _inputBuffer.Set(inputTick, input);
            }
        }

        void IServerController.Tick(int tick)
        {
            var hasInputs = behaviour.UseInputs();
            if (hasInputs)
            {
                // hostmode + host client has HasAuthority
                if (hostMode && behaviour.HasAuthority)
                {
                    var thisTickInput = behaviour.GetInput();
                    _inputBuffer.Set(tick, thisTickInput);
                }

                getValidInputs(tick, out var input, out var previous);
                behaviour.ApplyInputs(new NetworkInputs<TInput>(input, previous));
            }

            behaviour.NetworkFixedUpdate();

            if (hasInputs)
                _inputBuffer.Clear(tick - 1);
        }

        private void getValidInputs(int tick, out TInput input, out TInput previous)
        {
            input = default;
            previous = default;
            // dont need to do anything till first is received
            // skip check hostmode, there are always inputs for hostmode
            if (!hostMode && (lastReceived == null))
                return;

            getValidInput(tick, out var currentValid, out input);
            getValidInput(tick - 1, out var _, out previous);
            if (currentValid)
            {
                lastValidInput = (tick, input);
            }
        }

        private void getValidInput(int tick, out bool valid, out TInput input)
        {
            valid = _inputBuffer.TryGet(tick, out input);
            if (!valid)
            {
                if (logger.LogEnabled()) logger.Log($"No inputs for {tick}");
                input = behaviour.MissingInput(lastValidInput.input, lastValidInput.tick, tick);
            }
        }

        void IServerController.ReceiveHostInput<TInput2>(int tick, TInput2 _input)
        {
            // todo check Alloc from boxing
            if (_input is TInput input)
            {
                _inputBuffer.Set(tick, input);
            }
            else
            {
                throw new InvalidOperationException("Input type didn't match");
            }
        }
    }
}
