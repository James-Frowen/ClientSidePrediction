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

namespace JamesFrowen.CSP
{
    [NetworkMessage]
    struct WorldState
    {
        public int tick;
        public ArraySegment<byte> state;
    }

    [NetworkMessage]
    public struct InputMessage
    {
        public uint netId;
        public int tick;
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
