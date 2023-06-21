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
using System.Runtime.InteropServices;
using JamesFrowen.CSP.Alloc;
using Mirage.Logging;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.DeltaSnapshot
{
    /*
    If delta is large (over 16 bits)
    then write first 16 bits as normal
    then write other 16 bits as unsigned using zigzag

    might have to also use 1 bit to say we are using this encoding

    Need hoffman coding too!
    */

    public unsafe interface IDeltaSnapshot
    {
        void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to);
        void ReadDelta(NetworkReader reader, int intSize, int* from, int* to);
    }
    public unsafe class DeltaSnapshotWriter
    {
        private readonly IDeltaSnapshot _deltaSnapshot;
        private readonly IAllocator _allocator;
        private WorldStateCopy _zero;

        public DeltaSnapshotWriter(IAllocator allocator, IDeltaSnapshot deltaSnapshot = null)
        {
            _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
            _deltaSnapshot = deltaSnapshot ?? new DeltaSnapshot_ValueZeroCounts();
        }

        public void WriteDeltaVsZero(NetworkWriter writer, int intSize, int* to)
        {
            if (_zero == null)
                _zero = new WorldStateCopy();

            _zero.CheckSize(_allocator, intSize);
            for (var i = 0; i < intSize; i++)
            {
                if (_zero.Ptr[i] != 0)
                    throw new InvalidOperationException("Zero buffer was not zero");
            }

            _deltaSnapshot.WriteDelta(writer, intSize, _zero.Ptr, to);
        }

        public void ReadDeltaVsZero(NetworkReader reader, int intSize, int* to)
        {
            if (_zero == null)
                _zero = new WorldStateCopy();

            _zero.CheckSize(_allocator, intSize);

            _deltaSnapshot.ReadDelta(reader, intSize, _zero.Ptr, to);
        }

        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to) => _deltaSnapshot.WriteDelta(writer, intSize, from, to);
        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to) => _deltaSnapshot.ReadDelta(reader, intSize, from, to);


        [System.Obsolete("Use pointer instead", true)]
        public static void WriteDeltaVsZero(NetworkWriter writer, ArraySegment<byte> toSegment)
        {
            throw new NotSupportedException();
            //using (var temp = NetworkWriterPool.GetWriter())
            //{
            //    for (var i = 0; i < toSegment.Count; i++)
            //    {
            //        temp.WriteByte(0);
            //    }

            //    WriteDelta(writer, temp.ToArraySegment(), toSegment);
            //}
        }

        [System.Obsolete("Use pointer instead", true)]
        public static void WriteDelta(NetworkWriter writer, ArraySegment<byte> fromSegment, ArraySegment<byte> toSegment)
        {
            throw new NotSupportedException();
            //if (fromSegment.Count != toSegment.Count)
            //    throw new Exception("State size different");

            //var count = fromSegment.Count;
            //fixed (byte*
            //    fromByte = &fromSegment.Array[fromSegment.Offset],
            //    toByte = &toSegment.Array[toSegment.Offset])
            //{
            //    _deltaSnapshot.WriteDelta(writer, count, fromByte, toByte);
            //}
        }

        [System.Obsolete("Use pointer instead", true)]
        public static void ReadDeltaVsZero(NetworkReader reader, int count, NetworkWriter toWriter)
        {
            throw new NotSupportedException();
            //using (var temp = NetworkWriterPool.GetWriter())
            //{
            //    toWriter.Reset();
            //    for (var i = 0; i < count; i++)
            //    {
            //        temp.WriteByte(0);
            //        toWriter.WriteByte(0);
            //    }

            //    ReadDelta(reader, temp.ToArraySegment(), toWriter.ToArraySegment());
            //}
        }

        [System.Obsolete("Use pointer instead", true)]
        public static void ReadDelta(NetworkReader reader, ArraySegment<byte> fromSegment, ArraySegment<byte> toSegment)
        {
            throw new NotSupportedException();
            //if (fromSegment.Count != toSegment.Count)
            //    throw new Exception($"State size different from:{fromSegment.Count} to:{toSegment.Count}");

            //var count = fromSegment.Count;
            //fixed (byte*
            //    fromByte = &fromSegment.Array[fromSegment.Offset],
            //    toByte = &toSegment.Array[toSegment.Offset])
            //{
            //    _deltaSnapshot.ReadDelta(reader, count, fromByte, toByte);
            //}
        }
    }

    /// <summary>
    /// This is bad, but was quick to write
    /// </summary>
    public unsafe class DeltaSnapshot_IntDiffPack : IDeltaSnapshot
    {
        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to)
        {
            for (var i = 0; i < intSize; i++)
            {
                var diff = to[i] - from[i];
                var notZero = diff != 0;
                writer.WriteBoolean(notZero);
                if (notZero)
                {
                    writer.WritePackedInt32(diff);
                }
            }
        }

        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to)
        {
            for (var i = 0; i < intSize; i++)
            {
                var notZero = reader.ReadBoolean();
                if (notZero)
                {
                    var diff = reader.ReadPackedInt32();
                    to[i] = diff + from[i];
                }
                else
                {
                    to[i] = from[i];
                }
            }
        }
    }


    /// <summary>
    /// Writes number of zeros between each value
    /// </summary>
    public unsafe class DeltaSnapshot_ZeroCounts : IDeltaSnapshot
    {
        private static readonly ILogger logger = LogFactory.GetLogger<DeltaSnapshot_ZeroCounts>();

        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to)
        {
            uint zeroCount = 0;
            if (logger.LogEnabled()) logger.Log($"Int Count:{intSize}");
            for (var i = 0; i < intSize; i++)
            {
                var diff = to[i] - from[i];
                if (diff == 0)
                {
                    zeroCount++;
                }
                else
                {
                    if (logger.LogEnabled()) logger.Log($"Zero:{zeroCount} Diff:{diff}");
                    writer.WritePackedUInt32(zeroCount);
                    zeroCount = 0;
                    writer.WritePackedInt32(diff);
                }
            }
            if (zeroCount > 0)
            {
                if (logger.LogEnabled()) logger.Log($"Zero:{zeroCount} (last)");
                // write how many zeros we saw 
                writer.WritePackedUInt32(zeroCount);
            }
        }

        private static void WriteZero(NetworkWriter writer, ref uint zeroCount)
        {
            writer.WritePackedUInt32(zeroCount);
            zeroCount = 0;
        }


        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"IntCount:{intSize}");

            // if no ints, then dont read zero count, it will not have been written
            if (intSize <= 0)
                return;

            // need to read at start, then we skip that many 0s, then read value
            var zeroCount = reader.ReadPackedUInt32();
            if (logger.LogEnabled()) logger.Log($"Zero:{zeroCount} (first)");
            for (var i = 0; i < intSize; i++)
            {
                if (zeroCount == 0)
                {
                    // get diff, then get next zeroCount
                    var diff = reader.ReadPackedInt32();
                    to[i] = diff + from[i];

                    // special case when last value is not zero
                    // dont read extra zero if we are at last value
                    if (i < intSize - 1)
                    {
                        zeroCount = reader.ReadPackedUInt32();
                        if (logger.LogEnabled()) logger.Log($"Zero:{zeroCount} Diff:{diff}");
                    }
                }
                else
                {
                    to[i] = from[i];
                    zeroCount--;
                }
            }
            if (logger.IsLogTypeAllowed(LogType.Assert)) logger.Assert(zeroCount == 0, "Zero count should end at 0");
        }
    }

    /// <summary>
    /// Writes number of zeros or values between other groups of zeros values
    /// <para>[0,0,v1,v2,v3,0,0,0], then writes [2,3,3,v1,v2,v3]</para>
    /// </summary>
    public unsafe class DeltaSnapshot_ValueZeroCounts : IDeltaSnapshot
    {
        private static readonly ILogger logger = LogFactory.GetLogger<DeltaSnapshot_ValueZeroCounts>();

        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to)
        {
            // writes number of zero/values before there is a opposite 
            uint zeroCount = 0;
            // first is likely to be zero for delta because it will be netid that is unchanging
            var countingZeros = true;
            if (logger.LogEnabled()) logger.Log($"Int Count:{intSize}");

            var diff = stackalloc int[intSize];
            // write all zeros counts at start of message, then do 2nd loop to write values
            for (var i = 0; i < intSize; i++)
            {
                diff[i] = to[i] - from[i];
                var diffZero = diff[i] == 0;

                if (countingZeros == diffZero)
                {
                    zeroCount++;
                }
                else
                {
                    if (logger.LogEnabled())
                    {
                        if (countingZeros)
                            logger.Log($"Zero Count :{i}, {zeroCount}");
                        else
                            logger.Log($"Value Count:{i}, {zeroCount}");
                    }

                    writer.WritePackedUInt32(zeroCount);
                    zeroCount = 1;
                    countingZeros = !countingZeros;
                }
            }

            if (zeroCount > 0)
            {
                if (logger.LogEnabled())
                {
                    if (countingZeros)
                        logger.Log($"Zero Count : {zeroCount} (last)");
                    else
                        logger.Log($"Value Count: {zeroCount} (last)");
                }

                // write how many zeros we saw 
                writer.WritePackedUInt32(zeroCount);
            }

            for (var i = 0; i < intSize; i++)
            {
                var diffZero = diff[i] == 0;

                if (!diffZero)
                {
                    if (logger.LogEnabled()) logger.Log($"Diff[{i}]:{diff[i]}");
                    writer.WritePackedInt32(diff[i]);
                }
            }
        }


        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"IntCount:{intSize}");

            // if no ints, then dont read zero count, it will not have been written
            if (intSize <= 0)
                return;


            var readValue = stackalloc bool[intSize];
            var zeroCount = reader.ReadPackedUInt32();
            if (logger.LogEnabled()) logger.Log($"Zero Count : {zeroCount} (first)");

            var countingZeros = true;
            for (var i = 0; i < intSize; i++)
            {
                if (zeroCount == 0)
                {
                    countingZeros = !countingZeros;
                    zeroCount = reader.ReadPackedUInt32();

                    if (logger.LogEnabled())
                    {
                        if (countingZeros)
                            logger.Log($"Zero Count :{i}, {zeroCount}");
                        else
                            logger.Log($"Value Count:{i}, {zeroCount}");
                    }
                }


                readValue[i] = !countingZeros;
                zeroCount--;
            }
            if (logger.IsLogTypeAllowed(LogType.Assert)) logger.Assert(zeroCount == 0, "Zero count should end at 0");

            for (var i = 0; i < intSize; i++)
            {
                if (readValue[i])
                {
                    // get diff, then get next zeroCount
                    var diff = reader.ReadPackedInt32();
                    if (logger.LogEnabled()) logger.Log($"Diff[{i}]:{diff}");
                    to[i] = diff + from[i];
                }
                else
                {
                    to[i] = from[i];
                }
            }

        }
    }

    /// <summary>
    /// Focusing on delta and compressing float values. Is lossless
    /// </summary>
    public unsafe class DeltaSnapshot_FloatFocus : IDeltaSnapshot
    {
        private const int BLOCK_SIZE = 6;
        private static readonly ILogger logger = LogFactory.GetLogger<DeltaSnapshot_FloatFocus>();

        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"Int Count:{intSize}");

            for (var i = 0; i < intSize; i++)
            {
                var diff = to[i] - from[i];
                var isZero = diff == 0;
                writer.WriteBoolean(isZero);

                if (isZero)
                    continue;

                // we can zigzag encode int or float because sign bit will be in same position
                var zig = ZigZag.Encode(diff);
                var likelyFloat = zig > ushort.MaxValue;
                writer.WriteBoolean(likelyFloat);
                if (likelyFloat)
                {
                    // float layout,
                    // 1 bit sign
                    // 8 bit exponent
                    // 23 bit fraction

                    // smaller part of fraction will change a lot,
                    // so just write it
                    writer.WriteUInt16((ushort)zig);
                    var rest = zig >> 16;
                    VarIntBlocksPacker.Pack(writer, rest, BLOCK_SIZE);
                }
                else
                {
                    VarIntBlocksPacker.Pack(writer, zig, BLOCK_SIZE);
                }
            }
        }


        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"IntCount:{intSize}");

            // if no ints, then dont read zero count, it will not have been written
            if (intSize <= 0)
                return;


            for (var i = 0; i < intSize; i++)
            {
                var isZero = reader.ReadBoolean();
                if (isZero)
                {
                    to[i] = from[i];
                    continue;
                }

                var likelyFloat = reader.ReadBoolean();
                uint zig;
                if (likelyFloat)
                {
                    var smallFraction = (uint)reader.ReadUInt16();
                    var rest = (uint)VarIntBlocksPacker.Unpack(reader, BLOCK_SIZE);
                    zig = (rest << 16) | smallFraction;
                }
                else
                {
                    zig = (uint)VarIntBlocksPacker.Unpack(reader, BLOCK_SIZE);
                }

                var diff = ZigZag.Decode(zig);
                to[i] = diff + from[i];
            }
        }
    }

    /// <summary>
    /// Focusing on delta and compressing float values. IMPORTANT:LOSSY dont use on ints
    /// </summary>
    public unsafe class DeltaSnapshot_LossyFloats : IDeltaSnapshot
    {
        private const int BLOCK_SIZE = 6;
        private static readonly ILogger logger = LogFactory.GetLogger<DeltaSnapshot_LossyFloats>();

        private static string SplitFloat(float floatValue)
        {
            return SplitFloat(*(int*)&floatValue);
        }
        private static string SplitFloat(int value)
        {
            var sign = Sign(value);
            var exponent = Exponent(value);
            var fraction = Fraction(value);

            return $"[{sign}, {exponent:X2}, {fraction:X6}, {toBinary((int)sign, 1)}_{toBinary(exponent, 8)}_{toBinary(fraction, 23)}]";
        }
        private static string toBinary(int value, int padding = 32)
        {
            return Convert.ToString(value, 2).PadLeft(padding, '0');
        }

        private static uint Sign(int value)
        {
            return (uint)value >> 31;
        }
        private static int Exponent(int value)
        {
            return (value >> 23) & 0xFF;
        }
        private static int Fraction(int value)
        {
            return value & 0x7FFFFF;
        }
        private static int Float(uint sign, int exponent, int fraction)
        {
            return (int)(sign << 31) | ((exponent & 0xFF) << 23) | (fraction & 0x7FFFFF);
        }

        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"Int Count:{intSize}");
            WriteDelta_fr(writer, intSize, from, to);
        }
        public void WriteDelta_fr(NetworkWriter writer, int intSize, int* from, int* to)
        {
            /*
            From     :[0, 7A, 48DB42, 0b0_01111010_10010001101101101000010]
            To       :[0, 79, 5DB325, 0b0_01111001_10111011011001100100101]
            DiffFloat:[1, 79, 34035F, 0b1_01111001_01101000000001101011111]
            DiffInt  :[1, FF, 14D7E3, 0b1_11111111_00101001101011111100011]
             */

            var a = 0b0_01111010_10010001101101101000010;
            var b = 0b0_01111001_10111011011001100100101;

            var expA = Exponent(a);
            var expB = Exponent(b);
            var expDiff = expB - expA;

            int o;
            if (expDiff > 0)
            {
                var frac = Fraction(a) >> expDiff;
                o = Float(Sign(a), expB, frac);
            }
            else if (expDiff < 0)
            {
                var frac = Fraction(b) >> -expDiff;
                o = Float(Sign(b), expA, ~frac);
            }
            else
            {
                o = 0;
            }

            logger.Log($"" +
                $"A: {SplitFloat(a)} ({*(float*)&a:0.0000})\n" +
                $"B: {SplitFloat(b)} ({*(float*)&b:0.0000})\n" +
                $"O: {SplitFloat(o)} ({*(float*)&o:0.0000})\n" +
                $"");


            return;
            for (var i = 0; i < intSize; i++)
            {
                var expFrom = Exponent(from[i]);
                var expTo = Exponent(to[i]);

                writer.WriteBoolean(Sign(to[i]));

                //var expDiff = expTo - expFrom;

            }
        }
        public void WriteDelta_logging(NetworkWriter writer, int intSize, int* from, int* to)
        {
            /*
            From     :[0, 7A, 48DB42, 0x0_01111010_10010001101101101000010]
            To       :[0, 79, 5DB325, 0x0_01111001_10111011011001100100101]
            DiffFloat:[1, 79, 34035F, 0x1_01111001_01101000000001101011111]
            DiffInt  :[1, FF, 14D7E3, 0x1_11111111_00101001101011111100011]
             */

            for (var i = 0; i < intSize; i++)
            {
                var diffInt = to[i] - from[i];
                var fromFloat = *(float*)(from + i);
                var toFloat = *(float*)(to + i);



                var diffFloat = toFloat - fromFloat;
                var diffFloatAsInt = *(int*)&diffFloat;
                if (logger.LogEnabled())
                {
                    //logger.Log($"From:{fromFloat} To:{toFloat} Diff:{diffFloat} Hex:{diffFloatAsInt:X8} Binary:{Convert.ToString(diffFloatAsInt, 2).PadLeft(32, '0')}");

                    logger.Log($"From     :{SplitFloat(fromFloat)}\nTo       :{SplitFloat(toFloat)}\nDiffFloat:{SplitFloat(diffFloat)}\nDiffInt  :{SplitFloat(diffInt)}");
                }
                var isZero = diffInt == 0;
                writer.WriteBoolean(isZero);

                if (isZero)
                    continue;

                // we can zigzag encode int or float because sign bit will be in same position
                var zig = ZigZag.Encode(diffInt);
                var likelyFloat = zig > ushort.MaxValue;
                writer.WriteBoolean(likelyFloat);
                if (likelyFloat)
                {
                    // float layout,
                    // 1 bit sign
                    // 8 bit exponent
                    // 23 bit fraction

                    // smaller part of fraction will change a lot,
                    // so just write it
                    writer.WriteUInt16((ushort)zig);
                    var rest = zig >> 16;
                    VarIntBlocksPacker.Pack(writer, rest, BLOCK_SIZE);
                }
                else
                {
                    VarIntBlocksPacker.Pack(writer, zig, BLOCK_SIZE);
                }
            }
        }


        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"IntCount:{intSize}");

            // if no ints, then dont read zero count, it will not have been written
            if (intSize <= 0)
                return;


            for (var i = 0; i < intSize; i++)
            {
                var isZero = reader.ReadBoolean();
                if (isZero)
                {
                    to[i] = from[i];
                    continue;
                }

                var likelyFloat = reader.ReadBoolean();
                uint zig;
                if (likelyFloat)
                {
                    var smallFraction = (uint)reader.ReadUInt16();
                    var rest = (uint)VarIntBlocksPacker.Unpack(reader, BLOCK_SIZE);
                    zig = (rest << 16) | smallFraction;
                }
                else
                {
                    zig = (uint)VarIntBlocksPacker.Unpack(reader, BLOCK_SIZE);
                }

                var diff = ZigZag.Decode(zig);
                to[i] = diff + from[i];
            }
        }
    }

    /// <summary>
    /// Focusing on delta and compressing float values. IMPORTANT:LOSSY dont use on ints
    /// </summary>
    public unsafe class DeltaSnapshot_LossyFloats_Easy : IDeltaSnapshot
    {
        private const int BLOCK_SIZE = 6;
        private static readonly ILogger logger = LogFactory.GetLogger<DeltaSnapshot_LossyFloats_Easy>();

        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"Int Count:{intSize}");

            for (var i = 0; i < intSize; i++)
            {
                var fromFloat = *(float*)(from + i);
                var toFloat = *(float*)(to + i);
                var diffFloat = toFloat - fromFloat;

                var quantized = (int)(diffFloat * 10_000);
                var zig = ZigZag.Encode(quantized);
                VarIntBlocksPacker.Pack(writer, zig, BLOCK_SIZE);
            }
        }


        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to)
        {
            if (logger.LogEnabled()) logger.Log($"IntCount:{intSize}");

            // if no ints, then dont read zero count, it will not have been written
            if (intSize <= 0)
                return;

            for (var i = 0; i < intSize; i++)
            {
                var zig = VarIntBlocksPacker.Unpack(reader, BLOCK_SIZE);
                var quantized = ZigZag.Decode(zig);
                var diffFloat = quantized / 10_000f;

                var fromFloat = *(float*)(from + i);
                var toFloat = fromFloat + diffFloat;
                *(float*)(to + i) = toFloat;
            }
        }
    }

    /// <summary>
    /// Trying something
    /// </summary>
    public unsafe class DeltaSnapshot_FromObject
    {
        public struct State
        {
            public readonly void* from;
            public readonly void* to;
            public readonly int wordCount;
        }
        public void WriteDelta(NetworkWriter writer, List<State> worldState)
        {
            var totalCount = 0;
            foreach (var state in worldState)
                totalCount += state.wordCount;

            // first pass, check if changed
            var delta = stackalloc int[totalCount];
            var changed = stackalloc bool[worldState.Count];
            for (var sIndex = 0; sIndex < worldState.Count; sIndex++)
            {
                var state = worldState[sIndex];
                changed[sIndex] = false;
                var fromPtr = (int*)state.from;
                var toPtr = (int*)state.to;
                for (var j = 0; j < state.wordCount; j++)
                {
                    delta[j] = toPtr[j] - fromPtr[j];

                    if (delta[j] != 0)
                    {
                        changed[sIndex] = true;
                    }
                }

                writer.WriteBoolean(changed[sIndex]);
            }

            // second pass, write changed values
            var dIndex = 0;
            for (var sIndex = 0; sIndex < worldState.Count; sIndex++)
            {
                var wordCount = worldState[sIndex].wordCount;

                if (changed[sIndex])
                {
                    for (var i = 0; i < wordCount; i++, dIndex++)
                    {
                        writer.WritePackedInt32(delta[dIndex]);
                    }
                }
                else
                {
                    dIndex += wordCount;
                }
            }
            Debug.Assert(dIndex == totalCount);
        }

        public void ReadDelta(NetworkReader reader, List<State> worldState)
        {
            var totalCount = 0;
            foreach (var state in worldState)
                totalCount += state.wordCount;

            // first pass, check if changed
            var changed = stackalloc bool[worldState.Count];
            for (var sIndex = 0; sIndex < worldState.Count; sIndex++)
            {
                changed[sIndex] = reader.ReadBoolean();
            }

            // second pass, write changed values
            for (var sIndex = 0; sIndex < worldState.Count; sIndex++)
            {
                var state = worldState[sIndex];
                var fromPtr = (int*)state.from;
                var toPtr = (int*)state.to;
                var wordCount = state.wordCount;

                for (var i = 0; i < wordCount; i++)
                {
                    if (changed[sIndex])
                    {
                        var delta = reader.ReadPackedInt32();
                        toPtr[i] = fromPtr[i] + delta;
                    }
                    else
                    {
                        toPtr[i] = fromPtr[i];
                    }
                }
            }
        }
    }

    public unsafe class DeltaSnapshot_BinaryTree : IDeltaSnapshot
    {
        private const int BLOCK_SIZE = 6;
        private static readonly ILogger logger = LogFactory.GetLogger<DeltaSnapshot_LossyFloats_Easy>();


        public void WriteDelta_v1(NetworkWriter writer, int intSize, int* from, int* to)
        {
            var counts = stackalloc int*[10];
            var halfWay = stackalloc int[10];
            var current = 2;
            for (var i = 0; i < 10; i++)
            {
                var count0 = stackalloc int[current];
                counts[i] = count0;
                halfWay[i] = intSize / current;

                current *= 2;
            }
        }
        [StructLayout(LayoutKind.Explicit, Size = 8)]
        private struct Node
        {
            [FieldOffset(0)]
            public int ParentIndex;
            [FieldOffset(4)]
            public int Count;

            public Node(int parentIndex) : this()
            {
                ParentIndex = parentIndex;
            }
        }

        private Node[] _nodes;
        public void WriteDelta_v2(NetworkWriter writer, int intSize, int* from, int* to)
        {
            const int depth = 10;
            if (_nodes == null)
            {
                var count = 1 << (depth + 1 - 2);
                _nodes = new Node[count];

                // 2 root nodes, because we dont need to store the root node
                _nodes[0] = new Node(-1);
                _nodes[1] = new Node(-1);

                // Initialize child nodes
                for (var i = 0; i < count - 2; i++)
                {
                    var index = i + 2;
                    var parentIndex = i / 2;

                    _nodes[index] = new Node(parentIndex);
                }
            }

            var diff = stackalloc int[intSize];
            for (var i = 0; i < intSize; i++)
            {
                diff[i] = to[i] - from[i];
                var diffZero = diff[i] == 0;

                if (diffZero)
                    continue;

                var value = diff[i];

                // Traverse the tree and update counts
                var index = 2;
                while (index < _nodes.Length)
                {
                    _nodes[index].Count++;

                    // Update the parent node's count
                    var parentIndex = _nodes[index].ParentIndex;
                    if (parentIndex >= 0)
                    {
                        _nodes[parentIndex].Count++;
                    }

                    // Determine the next child index based on the value
                    index = (value < _nodes[index].Count) ? index * 2 : (index * 2) + 1;
                }
            }
        }

        //public void WriteDelta_v3_slow(NetworkWriter writer, int intSize, int* from, int* to)
        public void WriteDelta(NetworkWriter writer, int intSize, int* from, int* to)
        {
            var diff = stackalloc int[intSize];
            for (var i = 0; i < intSize; i++)
            {
                diff[i] = to[i] - from[i];
                var diffZero = diff[i] == 0;
            }
            WriteLeftRight(writer, intSize, diff);
        }

        private static void WriteLeftRight(NetworkWriter writer, int intSize, int* diff)
        {
            if (intSize == 1)
            {
                writer.WritePackedInt32(diff[0]);
                return;
            }

            var halfway = intSize / 2;
            WriteRecursive(writer, halfway, diff);
            WriteRecursive(writer, halfway, diff + halfway);
        }
        private static void WriteRecursive(NetworkWriter writer, int intSize, int* diff)
        {
            for (var i = 0; i < intSize; i++)
            {
                if (diff[i] == 0)
                    continue;

                writer.WriteBoolean(true);
                WriteLeftRight(writer, intSize, diff);
                return;
            }

            writer.WriteBoolean(false);
        }

        public void ReadDelta(NetworkReader reader, int intSize, int* from, int* to)
        {
            var diff = stackalloc int[intSize];
            ReadLeftRight(reader, intSize, diff);
            for (var i = 0; i < intSize; i++)
            {
                to[i] = from[i] + diff[i];
            }
        }
        private static void ReadLeftRight(NetworkReader reader, int intSize, int* diff)
        {
            if (intSize == 1)
            {
                diff[0] = reader.ReadPackedInt32();
                return;
            }

            var halfway = intSize / 2;
            ReadRecursive(reader, halfway, diff);
            ReadRecursive(reader, halfway, diff + halfway);
        }

        private static void ReadRecursive(NetworkReader reader, int intSize, int* diff)
        {
            for (var i = 0; i < intSize; i++)
            {
                var diffNonZero = reader.ReadBoolean();
                if (!diffNonZero)
                    continue;

                ReadLeftRight(reader, intSize, diff);
                return;
            }

            // All differences are zero
            for (var i = 0; i < intSize; i++)
            {
                diff[i] = 0;
            }
        }
    }
}
