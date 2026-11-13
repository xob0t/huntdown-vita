using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine.Experimental.LowLevel;

// Where the engine's frame goes. The console has no sampling profiler, and the heartbeat's ms= is the whole
// frame: with a few hundred sprites on screen it passes 16.7 ms, and the game's own code (compat/Prof.cs) is
// 3 ms of it. -hunt-phases puts a timestamp between every step of Unity's player loop (PlayerLoop: its
// Initialization, EarlyUpdate, FixedUpdate, PreUpdate, Update, PreLateUpdate and PostLateUpdate, and each one's
// steps, such as Update.ScriptRunBehaviourUpdate, the game's Updates, or PostLateUpdate.FinishFrameRendering,
// the cameras' rendering), and each heartbeat logs the steps that took the most, in ms per frame (HUNT_PHASES).
// "(between frames)" is the time from the loop's last step to the next frame's first: the wait for vsync, if
// the engine's present step doesn't wait itself.
//   -hunt-phases   the HUNT_PHASES lines (with -hunt-heartbeat)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Phases
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static readonly List<string> names = new List<string>();
        static long[] sums;
        static int current = -1;
        static long last;

        internal static void Install(string[] args)
        {
            if (!HuntFlags.Has(args, "-hunt-phases")) return;
            PlayerLoopSystem loop = PlayerLoop.GetDefaultPlayerLoop();
            PlayerLoopSystem[] top = loop.subSystemList;
            for (int t = 0; t < top.Length; t++)
            {
                string topName = top[t].type == null ? "?" : top[t].type.Name;
                PlayerLoopSystem[] steps = top[t].subSystemList ?? new PlayerLoopSystem[0];
                var timed = new List<PlayerLoopSystem>();
                foreach (PlayerLoopSystem step in steps)
                {
                    timed.Add(Marker(topName + "." + (step.type == null ? "?" : step.type.Name)));
                    timed.Add(step);
                }
                timed.Add(Marker(t == top.Length - 1 ? "(between frames)" : topName + " (end)"));
                top[t].subSystemList = timed.ToArray();
            }
            loop.subSystemList = top;
            sums = new long[names.Count];
            PlayerLoop.SetPlayerLoop(loop);
            UnityEngine.Debug.Log("HUNT_PHASES timing " + names.Count + " steps of the player loop");
        }

        static PlayerLoopSystem Marker(string name)
        {
            int index = names.Count;
            names.Add(name);
            return new PlayerLoopSystem { type = typeof(Phases), updateDelegate = () => Mark(index) };
        }

        static void Mark(int next)
        {
            long now = clock.ElapsedTicks;
            if (current >= 0) sums[current] += now - last;
            current = next;
            last = clock.ElapsedTicks;  // the marker's own time left out
        }

        // The steps that took 0.1 ms a frame or more, the most first, since the last report; null without -hunt-phases.
        internal static string Report(int frames)
        {
            if (sums == null || frames <= 0) return null;
            double total = sums.Sum() * MsPerTick / frames;
            var line = new StringBuilder("HUNT_PHASES ms/frame total=").Append(total.ToString("F2", Inv));
            foreach (int i in Enumerable.Range(0, sums.Length).OrderByDescending(i => sums[i]))
            {
                double ms = sums[i] * MsPerTick / frames;
                if (ms < 0.1) break;
                line.Append(' ').Append(names[i].Replace(' ', '_')).Append('=').Append(ms.ToString("F2", Inv));
            }
            for (int i = 0; i < sums.Length; i++) sums[i] = 0;
            return line.ToString();
        }
    }
}
