/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

namespace JamesFrowen.CSP
{
    internal class Helper
    {
        public const int NO_VALUE = -1;


        // 256 is probably too bug, but is fine for example
        public const int BufferSize = 256;
        [System.Obsolete("Use Ring buffer instead")]
        public static int TickToBuffer(int tick)
        {
            //negative
            if (tick < 0)
                tick += BufferSize;
            return tick % BufferSize;
        }
    }
}
