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
using JamesFrowen.DeltaSnapshot;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.CSP
{
    internal class ServerInputHandler
    {
        private static readonly ILogger logger = LogFactory.GetLogger("JamesFrowen.CSP.ServerInputHandler");
        private static readonly ILogger verbose = LogFactory.GetLogger("JamesFrowen.CSP.ServerInputHandler_Verbose", LogType.Exception);

        private readonly Dictionary<INetworkPlayer, PlayerTimeTracker> _playerTracker;
        private readonly NetworkWorld _world;

        public ServerInputHandler(Dictionary<INetworkPlayer, PlayerTimeTracker> playerTracker, NetworkWorld world)
        {
            _playerTracker = playerTracker;
            _world = world;
        }

        public void HandleNotReady(INetworkPlayer player, InputStateNotReady message)
        {
            var tracker = _playerTracker[player];
            tracker.LastReceivedClientTime = Math.Max(tracker.LastReceivedClientTime, message.ClientTime);
            tracker.ReadyForWorldState = false;
        }

        public void HandleInput(INetworkPlayer player, InputState message, int lastSimTick)
        {
            var tracker = _playerTracker[player];
            tracker.LastReceivedClientTime = Math.Max(tracker.LastReceivedClientTime, message.ClientTime);
            // check if inputs have arrived in time and in order, otherwise we can't do anything with them.
            if (!ValidateInputTick(tracker, message.Tick, lastSimTick))
                return;

            tracker.ReadyForWorldState = true;

            ReadInputs(player, message, tracker, lastSimTick);

            tracker.lastReceivedInput = Mathf.Max(tracker.lastReceivedInput.GetValueOrDefault(), message.Tick);
        }

        private void ReadInputs(INetworkPlayer player, InputState message, PlayerTimeTracker tracker, int lastSimTick)
        {
            var length = message.NumberOfInputs;
            using (var reader = NetworkReaderPool.GetReader(message.Payload, _world))
            {
                // keep reading while there is atleast 1 byte
                // netBehaviour will be alteast 1 byte
                while (reader.CanReadBytes(1))
                {
                    var networkBehaviour = reader.ReadNetworkBehaviour();
                    if (networkBehaviour == null)
                    {
                        if (logger.WarnEnabled()) logger.LogWarning($"Spawned object not found when handling InputMessage message");
                        return;
                    }

                    if (player != networkBehaviour.Owner)
                        throw new InvalidOperationException($"player {player} does not have authority to set inputs for object. Object[Netid:{networkBehaviour.NetId}, name:{networkBehaviour.name}]");

                    if (!(networkBehaviour is IPredictionBehaviour behaviour))
                        throw new InvalidOperationException($"Networkbehaviour({networkBehaviour.NetId}, {networkBehaviour.ComponentIndex}) was not a IPredictionBehaviour");

                    var inputTick = message.Tick;
                    for (var i = 0; i < length; i++)
                    {
                        var t = inputTick - i;
                        behaviour.ServerController.ReadInput(tracker, reader, t, lastSimTick);
                    }
                }
            }
        }

        private bool ValidateInputTick(PlayerTimeTracker tracker, int tick, int lastSimTick)
        {
            // received inputs out of order
            // we can ignore them, input[n+1] will contain input[n], so we would have no new inputs in this packet
            if (tracker.lastReceivedInput > tick)
            {
                if (logger.LogEnabled()) logger.Log($"received inputs out of order, lastReceived:{tracker.lastReceivedInput} new inputs:{tick}");
                return false;
            }

            // if lastTick is before last sim, then it is late and we can't use
            if (tick >= lastSimTick)
            {
                if (verbose.LogEnabled()) verbose.Log($"received inputs for {tick}. lastSim:{lastSimTick}. early by {tick - lastSimTick}");
                return true;
            }

            if (logger.LogEnabled())
            {
                logger.Log($"received inputs <color=red>Late</color> for {tick}, lastSim:{lastSimTick}. late by {lastSimTick - tick}"
                    + (tracker.lastReceivedInput == null ? ". But was at start, so not a problem" : ""));
            }

            return false;
        }
    }
}
