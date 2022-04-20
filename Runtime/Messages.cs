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
    [NetworkMessage]
    struct WorldState
    {
        public int tick;
        /// <summary>
        /// Send the last received time back to the client
        /// <para>This will be used by the client to caculate its local time</para>
        /// </summary>
        public double ClientTime;
        public ArraySegment<byte> state;
    }

    /// <summary>
    /// All inputs for client
    /// </summary>
    [NetworkMessage]
    public struct InputState
    {
        public int tick;
        public double clientTime;

        [BitCountFromRange(1, 8)]
        public int length;
        /// <summary>
        /// collection of <see cref="InputMessage"/>
        /// </summary>
        public ArraySegment<byte> payload;
    }

    public enum SimulationMode
    {
        Physics3D,
        Physics2D,
        Local3D,
        Local2D,
    }
}
