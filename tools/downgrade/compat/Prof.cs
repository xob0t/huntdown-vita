using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

// Where a frame's time goes in the game's own code: in a build made with HUNT_PROF=1 (a code-stage build
// switch; quickcode takes it), the rewriter (rewriter/Patches.cs, ProfileFrameMethods) times every
// FrameEvent, Update, LateUpdate and FixedUpdate of the game's classes, the methods its frame runs through
// (Main.Update -> Game.FrameEvent -> each object's FrameEvent). Times include the calls inside: the tree's
// top shows the game's whole frame, the levels below what it is spent on. Each heartbeat (-hunt-heartbeat)
// logs the methods that took the most, per frame, and resets (HUNT_PROF lines); the heartbeat's own ms= is
// the whole frame, so what the game's code doesn't take is the engine's (rendering, the sprites'
// transforms and batching) and the wait for vsync.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Prof
    {
        sealed class Entry
        {
            internal long ticks;
            internal int calls;
        }

        static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // True once an instrumented method has run: the build has the profiler in.
        internal static bool Active { get { return entries.Count > 0; } }

        public static long Enter()
        {
            return Stopwatch.GetTimestamp();
        }

        public static void Exit(string name, long start)
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            Entry entry;
            if (!entries.TryGetValue(name, out entry)) entries[name] = entry = new Entry();
            entry.ticks += elapsed;
            entry.calls++;
        }

        // "HUNT_PROF <frames> frames: Main.Update 9.81 ms (1), Game.FrameEvent 8.02 ms (1), ..." for the top
        // methods by time per frame since the last report, then starts again.
        internal static string Report(int frames, int top = 14)
        {
            if (frames <= 0 || entries.Count == 0) return null;
            var line = new StringBuilder("HUNT_PROF " + frames + " frames:");
            foreach (KeyValuePair<string, Entry> pair in entries.OrderByDescending(p => p.Value.ticks).Take(top))
            {
                if (pair.Value.ticks == 0) break;
                line.Append(' ').Append(pair.Key).Append(' ')
                    .Append((pair.Value.ticks * MsPerTick / frames).ToString("F2", Inv)).Append(" ms (")
                    .Append(((double)pair.Value.calls / frames).ToString("F0", Inv)).Append("),");
            }
            foreach (Entry entry in entries.Values)
            {
                entry.ticks = 0;
                entry.calls = 0;
            }
            return line.ToString().TrimEnd(',');
        }
    }
}
