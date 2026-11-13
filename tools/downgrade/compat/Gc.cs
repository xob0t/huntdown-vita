using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

// When the garbage collector runs. IL2CPP's collector (Boehm's) stops the game and goes through the whole managed
// heap, 18-24 MB in a level, which takes the PS Vita 0.15-0.2 s. It runs after a MB or two of allocations, and
// the game allocates about 0.2 KB a frame in play: a stop about once a minute. On the PS Vita it is held
// (GC_disable) and collects only where a stop doesn't show: when a level starts or restarts from a checkpoint
// (Game.InitLevel, behind the load or the fade, with LevelMinMB or more: a level's load leaves 3-12 MB, a restart
// after a death about 1) and when the pause menu opens (Game.PauseGame, with PauseMinMB or more). Play with no death, pause or new level collects where it is at CapMB, about half an hour
// of it. The heap holds up to CapMB of garbage more, from the 160-190 MB of main memory free in a level. The
// rewriter (rewriter/Patches.cs, PatchGcSafePoints) has those two methods call LevelStart and Paused first.
//   -hunt-gc game|held   game: the collector's own timing (other players' default); held: the PS Vita's default
//   -hunt-gc-cap <MB>    with held, the garbage that collects anywhere (default 24)
// Each collection while held logs a HUNT_GC line with its time.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Gc
    {
        const float CapMB = 24f, LevelMinMB = 2f, PauseMinMB = 4f;
        const long MB = 1024 * 1024;
        static bool held;
        static long cap;

        // Boehm's own calls, linked into the PS Vita's code module (il2CppAssemblies.suprx) with the collector.
        [DllImport("__Internal")] static extern void GC_disable();
        [DllImport("__Internal")] static extern void GC_enable();
        [DllImport("__Internal")] static extern UIntPtr GC_get_bytes_since_gc();
        [DllImport("__Internal")] static extern UIntPtr GC_get_total_bytes();

        internal static void Install(string[] args)
        {
            if (!HuntFlags.Vita || HuntFlags.Value(args, "-hunt-gc", "held") != "held") return;
            cap = (long)(HuntFlags.Number(args, "-hunt-gc-cap", CapMB) * MB);
            GC_disable();
            held = true;
            Application.onBeforeRender += CheckCap;
            Debug.Log("HUNT_GC held: collects at level starts, pauses and " + cap / MB + " MB of garbage");
        }

        // Bytes allocated since the player started (the PS Vita's; elsewhere the heap's size, which a
        // collection takes down), for Prof's allocation mode.
        internal static long Allocated()
        {
            return HuntFlags.Vita ? (long)GC_get_total_bytes() : GC.GetTotalMemory(false);
        }

        // Game.InitLevel: a level's start, or its restart from a checkpoint.
        public static void LevelStart()
        {
            if (held) Collect("level", (long)(LevelMinMB * MB));
        }

        // Game.PauseGame(bool): the player paused (or the menu opens on its own, the controller gone).
        public static void Paused()
        {
            if (held) Collect("pause", (long)(PauseMinMB * MB));
        }

        static void CheckCap()
        {
            Collect("cap", cap);
        }

        static void Collect(string where, long least)
        {
            long garbage = (long)GC_get_bytes_since_gc();
            if (garbage < least) return;
            long before = GC.GetTotalMemory(false);
            var watch = Stopwatch.StartNew();
            GC_enable();
            GC.Collect();
            GC_disable();
            Debug.Log("HUNT_GC " + where + ": " + watch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms, " +
                      garbage / 1024 + " KB allocated since the last, heap " + Mb(before) + " -> " + Mb(GC.GetTotalMemory(false)) + " MB");
        }

        static string Mb(long bytes)
        {
            return (bytes / (double)MB).ToString("F1", CultureInfo.InvariantCulture);
        }
    }
}
