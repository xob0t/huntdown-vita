using System;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using Resources = UnityEngine.Resources;

// The game's synchronous Resources loads, timed. The rewriter (rewriter/Patches.cs, RouteResourceLoads) sends every
// Resources.Load and Resources.LoadAll call in the game's code here; each does what it did and logs a HUNT_LOADTIME
// line when it took SlowMs or more: the call, the path and its time. On the PS Vita those loads read from the
// memory card on the main thread and stalled frames for up to 1.3 s; the lines say which to read ahead
// (compat/Preload.cs). A stall dump's stack stops at Resources.LoadAll, without the game's caller.
//   -hunt-load-ms <ms>   log loads of ms or more (default 30; 0 logs none)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class LoadTimer
    {
        static int? slowMs;

        static int SlowMs
        {
            get
            {
                if (slowMs == null) slowMs = HuntFlags.Int(ProbeFiles.Args, "-hunt-load-ms", 30);
                return slowMs.Value;
            }
        }

        static void Report(string call, string path, long started)
        {
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (SlowMs > 0 && ms >= SlowMs)
                Debug.Log("HUNT_LOADTIME " + call + " " + path + " " + ms.ToString("F0") + " ms at " +
                          UnityEngine.Time.realtimeSinceStartup.ToString("F2") + " s");
        }

        public static T Load<T>(string path) where T : Object
        {
            long started = Stopwatch.GetTimestamp();
            T result = Resources.Load<T>(path);
            Report("Load<" + typeof(T).Name + ">", path, started);
            return result;
        }

        public static T[] LoadAll<T>(string path) where T : Object
        {
            long started = Stopwatch.GetTimestamp();
            T[] result = Resources.LoadAll<T>(path);
            Report("LoadAll<" + typeof(T).Name + ">", path, started);
            return result;
        }

        public static Object Load(string path)
        {
            long started = Stopwatch.GetTimestamp();
            Object result = Resources.Load(path);
            Report("Load", path, started);
            return result;
        }

        public static Object Load(string path, Type type)
        {
            long started = Stopwatch.GetTimestamp();
            Object result = Resources.Load(path, type);
            Report("Load(" + type.Name + ")", path, started);
            return result;
        }

        public static Object[] LoadAll(string path)
        {
            long started = Stopwatch.GetTimestamp();
            Object[] result = Resources.LoadAll(path);
            Report("LoadAll", path, started);
            return result;
        }

        public static Object[] LoadAll(string path, Type type)
        {
            long started = Stopwatch.GetTimestamp();
            Object[] result = Resources.LoadAll(path, type);
            Report("LoadAll(" + type.Name + ")", path, started);
            return result;
        }
    }
}
