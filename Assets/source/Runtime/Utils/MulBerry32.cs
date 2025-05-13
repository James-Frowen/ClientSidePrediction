/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using UnityEngine;

namespace JamesFrowen.CSP
{
    /// <summary>
    /// Creates random numbers from a seed value, can be used to create by Client Side Prediction to create same values on client and server
    /// <para>
    /// For example: using previous state to get next "random" state
    /// </para>
    /// </summary>
    public struct RNG
    {
        private uint _seed;
        public RNG(uint seed) => _seed = seed;
        public RNG(int seed) => _seed = (uint)seed;
        public unsafe RNG(float seed) => _seed = *(uint*)&seed;

        // MulBerry32 is under public domain
        // see: https://gist.github.com/tommyettinger/46a874533244883189143505d203312c
        public static uint MulBerry32(uint z)
        {
            z += 0x6D2B79F5;
            z = (z ^ (z >> 15)) * (1 | z);
            z ^= z + ((z ^ (z >> 7)) * (61 | z));
            return z ^ (z >> 14);
        }

        /// <summary>Value from 0 to 1</summary>
        public float Next()
        {
            _seed = MulBerry32(_seed);
            // important: divide by float, so we get value 0->1
            //            this will divide by the uint max value, as a float, so next/4 billion
            return (float)(_seed / (float)uint.MaxValue);
        }

        public float Next(float min, float max)
        {
            return min + ((max - min) * Next());
        }

        public Vector3 OnUnitSphere()
        {
            var x = Next();
            var y = Next();
            var z = Next();
            return new Vector3(x, y, z).normalized;
        }

        public Vector3 InsideUnitSphere()
        {
            var x = Next();
            var y = Next();
            var z = Next();
            var v = new Vector3(x, y, z);
            if (v.sqrMagnitude > 1)
                return v.normalized;
            else
                return v;
        }

        /// <summary>Value from 0 to 1</summary>
        public static unsafe float Next(float seed)
        {
            var next = MulBerry32(*(uint*)&seed);
            // important: divide by float, so we get value 0->1
            //            this will divide by the uint max value, as a float, so next/4 billion
            return next / (float)uint.MaxValue;
        }

        public static unsafe float Next(float seed, float min, float max)
        {
            return min + ((max - min) * Next(seed));
        }
    }
}
