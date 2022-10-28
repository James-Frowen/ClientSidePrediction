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
using System.IO;
using System.Linq;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.DeltaSnapshot
{
    public class HuffmanCoding
    {


        //public static void Train(int* ptr, int intSize)
    }

    public class Node<T>
    {
        public readonly bool IsLeaf;

        public T Data;
        public Node<T> Parent;
        public Node<T> Left;
        public Node<T> Right;

        public Node(bool isLeaf)
        {
            IsLeaf = isLeaf;
        }

        public Node(Node<T> left, Node<T> right)
        {
            IsLeaf = false;

            ConnectLeft(left);
            ConnectRight(right);
        }

        public Node<T> AddParent(T data = default)
        {
            if (!IsLeaf && (Left == null || Right == null))
                throw new InvalidOperationException("Only leafs can have no children");

            var node = new Node<T>(false)
            {
                Parent = Parent,
                Data = data,
                Right = this,
            };


            Parent = node;
            return node;
        }


        public Node<T> AddLeft(T data = default)
        {
            return Create(ref Left, data);
        }

        public Node<T> AddRight(T data = default)
        {
            return Create(ref Right, data);
        }

        public void ConnectLeft(Node<T> left)
        {
            Connect(ref Left, left);
        }

        public void ConnectRight(Node<T> right)
        {
            Connect(ref Right, right);
        }

        private Node<T> Create(ref Node<T> node, T data)
        {
            if (node != null)
                throw new InvalidOperationException("node was not null");

            node = new Node<T>(true)
            {
                Data = data,
                Parent = this
            };
            return node;
        }

        private void Connect(ref Node<T> field, Node<T> newNode)
        {
            if (field != null)
                throw new InvalidOperationException("node was not null");

            newNode.Parent = this;
            field = newNode;
        }
    }

    public class FrequencySorter : IComparer<Node<(int bits, int count)>>
    {
        public int Compare(Node<(int bits, int count)> x, Node<(int bits, int count)> y)
        {
            return x.Data.count.CompareTo(y.Data.count);
        }
    }
    public unsafe class HuffmanCodingDebugging
    {
        public static void Count()
        {
            var raw = LoadRaw();
            var frequencies = new int[33];
            CountSizes(raw, frequencies);
            var frequencyDictionary = Group(frequencies, 8);

            var orderedFrequency = frequencyDictionary.OrderBy(kvp => kvp.Value).Select(x => (bits: x.Key, count: x.Value)).ToArray();
            foreach (var pair in orderedFrequency)
            {
                var bits = pair.bits;
                var count = pair.count;
                Debug.Log($"Size:{bits:D2}, Count:{count}");
            }

            CreateTree(frequencyDictionary);
        }

        private static Dictionary<int, int> Group(int[] frequencies, int groupSize)
        {
            var frequencyDictionary = new Dictionary<int, int>();
            var groupSizeMinusOne = groupSize - 1;
            for (var bits = 0; bits < 33; bits++)
            {
                var count = frequencies[bits];
                //Debug.Log($"Size:{bits:D2}, Count:{count}");

                var group = ((bits + groupSizeMinusOne) / groupSize) * groupSize;
                if (!frequencyDictionary.ContainsKey(group))
                    frequencyDictionary.Add(group, 0);

                frequencyDictionary[group] += frequencies[bits];
            }

            return frequencyDictionary;
        }

        private static void CreateTree(Dictionary<int, int> frequencyDictionary)
        {
            Debug.Log($"---Tree---");

            //for (var i = 0; i < orderedFrequency.Length; i++)
            //{
            //    var frequency = orderedFrequency[i];
            //    var leaf =
            //    if (root == null)
            //    {
            //        root = new Node<(int bits, int count)>(true);
            //        root.Data = frequency;
            //        continue;
            //    }

            //    root = root.AddParent();
            //    root.AddLeft(frequency);
            //    root.Data.count = root.Left.Data.count + root.Right.Data.count;
            //}

            var unconnectedNodes = new List<Node<(int bits, int count)>>();
            foreach (var kvp in frequencyDictionary)
            {
                var leaf = new Node<(int bits, int count)>(true);
                leaf.Data = (bits: kvp.Key, count: kvp.Value);
                unconnectedNodes.Add(leaf);
            }

            var sorter = new FrequencySorter();
            while (unconnectedNodes.Count > 1)
            {
                unconnectedNodes.Sort(sorter);

                // take smallest 2 and connect
                var right = unconnectedNodes[0];
                var left = unconnectedNodes[1];

                var parent = new Node<(int bits, int count)>(left, right);
                parent.Data.count = right.Data.count + left.Data.count;

                unconnectedNodes.RemoveRange(0, 2);
                unconnectedNodes.Add(parent);
            }

            var root = unconnectedNodes[0];
            Walk(root);
        }

        private static void Walk(Node<(int bits, int count)> node, string prefix = "")
        {
            if (node.IsLeaf)
            {
                var bits = node.Data.bits;
                var count = node.Data.count;
                Debug.Log($"Size:{bits:D2}, count:{count}, prefix:{prefix}");
            }
            else
            {
                Walk(node.Left, prefix + "0");
                Walk(node.Right, prefix + "1");
            }
        }

        private static List<byte[]> LoadRaw()
        {
            var frameCount = 896;
            var rawFrames = new List<byte[]>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var path = $"./WorldState/{i:D4}.data";
                if (!File.Exists(path))
                    continue;
                var raw = File.ReadAllBytes(path);
                rawFrames.Add(raw);

                if (raw.Length % 4 != 0)
                {
                    throw new Exception($"total bytes was not multiple of 4");
                }
            }
            return rawFrames;
        }
        private static byte[] MergeRaw(List<byte[]> rawFrames)
        {
            var totalBytes = 0;
            for (var i = 0; i < rawFrames.Count; i++)
                totalBytes += rawFrames[i].Length;

            var mergedRaw = new byte[totalBytes];
            var offset = 0;
            for (var i = 0; i < rawFrames.Count; i++)
            {
                Buffer.BlockCopy(rawFrames[i], 0, mergedRaw, offset, rawFrames[i].Length);
                offset += rawFrames[i].Length;
            }
            return mergedRaw;
        }

        private static void CountSizes(List<byte[]> rawFrames, int[] frequency)
        {
            foreach (var frame in rawFrames)
            {
                CountSizes(frame, frequency);
            }
        }

        private static int[] CountSizes(byte[] raw, int[] frequency)
        {
            fixed (byte* bPtr = &raw[0])
            {
                var ptr = (int*)bPtr;
                CountSizes(ptr, raw.Length / 4, frequency);
            }

            return frequency;
        }

        private static void CountSizes(int* ptr, int intCount, int[] frequency)
        {
            for (var i = 0; i < intCount; i++)
            {
                var value = ptr[i];
                var zigzag = ZigZag.Encode(value);

                if (zigzag == 0)
                {
                    frequency[0]++;
                }
                else
                {
                    var count = BitHelper.BitCount(zigzag);
                    frequency[count]++;
                }
            }
        }
    }
}
