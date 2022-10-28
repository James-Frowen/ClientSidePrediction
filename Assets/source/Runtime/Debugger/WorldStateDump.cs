/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace JamesFrowen.CSP.Debugging
{
    internal static unsafe class WorldStateDump
    {
        [ThreadStatic] private static byte[] buffer;

        private static string _dir;
        private static string Dir => _dir ?? (_dir = Path.Combine(Application.persistentDataPath, "WorldState"));
        private static string PathFromTick(int tick) => Path.Combine(Dir, $"{tick:D4}.data");

        public static void ToFile(int tick, int* ptr, int intSize)
        {
            CheckDir(Dir);
            UniTask.RunOnThreadPool(() => ToFileInternal(tick, ptr, intSize)).Forget();
        }

        private static void ToFileInternal(int tick, int* ptr, int intSize)
        {
            try
            {
                if (buffer == null)
                    buffer = new byte[intSize * 4];
                if (buffer.Length < intSize * 4)
                    Array.Resize(ref buffer, intSize * 4);

                fixed (byte* bPtr = &buffer[0])
                {
                    var b = (int*)bPtr;

                    for (var i = 0; i < intSize; i++)
                    {
                        b[i] = ptr[i];
                    }
                }

                var path = PathFromTick(tick);
                File.WriteAllBytes(path, buffer);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private static void CheckDir(string dir)
        {
            if (Directory.Exists(dir))
                return;

            Directory.CreateDirectory(dir);
        }

        public static byte[] FromFile(int tick)
        {
            var path = PathFromTick(tick);
            return File.ReadAllBytes(path);
        }

        public static void ClearFolder()
        {
            var path = PathFromTick(0);
            var dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                return;

            // delete previous if exists
            var dirPrev = dir + "_prev";
            if (Directory.Exists(dirPrev))
                Directory.Delete(dirPrev, true);

            Directory.Move(dir, dir + "_prev");
        }
    }
}
