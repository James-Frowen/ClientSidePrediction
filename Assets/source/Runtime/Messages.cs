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
using Mirage.Serialization;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// All inputs for client
    /// </summary>
    [NetworkMessage]
    internal struct InputState
    {
        public int Tick;
        public double ClientTime;

        /// <summary>
        /// How many inputs were sent in payload
        /// </summary>
        [BitCountFromRange(1, 8)]
        public int NumberOfInputs;

        /// <summary>
        /// collection of <see cref="InputMessage"/>
        /// </summary>
        public ArraySegment<byte> Payload;
    }

    /// <summary>
    /// Message sent by client so it can be send its <see cref="ClientTime"/> back with <see cref="DeltaWorldState"/>
    /// </summary>
    [NetworkMessage]
    internal struct InputStateNotReady
    {
        public double ClientTime;
    }

    /// <summary>
    /// Send by client to so that server can send it the most recent time info.
    /// Client will then reset its tickrunner to match timer. this should happen at the start so to correctly timings after loading
    /// </summary>
    [NetworkMessage]
    internal struct RequestTimeInfo
    {
        public double ClientTime;
    }

    /// <summary>
    /// reply for <see cref="RequestTimeInfo"/>
    /// </summary>
    [NetworkMessage]
    public struct TimeInfo
    {
        public int Tick;

        /// <summary>
        /// Time scale on server, null if default value of 1
        /// </summary>
        public float? TimeScale;

        /// <summary>
        /// Send the last received time back to the client
        /// <para>This will be used by the client to caculate its local time</para>
        /// </summary>
        public double ClientTime;
    }

    public enum SimulationMode
    {
        Physics3D,
        Physics2D,
        Local3D,
        Local2D,
    }
}
