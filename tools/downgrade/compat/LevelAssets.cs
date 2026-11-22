using System.Globalization;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;

// The assets a level leaves behind, freed when the next one is made. Gfx.StartLevelGfx instantiates the level's
// scenery from two AssetBundles (its area's backgrounds and DevLoader's Level_<a>-<l> prefab) and unloads both
// 0.3 s later with Unload(false), which keeps what it loaded: the scenery's textures and sprites stay, owned by
// no bundle. When the level goes (Gfx.ClearLevelGfx destroys its GameObjects), nothing frees them, and the next
// load reads new copies: the game is one scene and never calls Resources.UnloadUnusedAssets, which Unity does
// only on a scene load. Each RESTART LEVEL from the pause menu (a reload, not a checkpoint restart), each next
// level and each boss stage kept the last one's scenery: level 1-1's is about 18 MB, mostly in the PS Vita's
// 8 MB of free video memory (CDRAM), the rest in main memory: after 2 restarts CDRAM was full, after 3 main memory
// had 110 MB of 158 left, and the player's report was a level that stopped loading its graphics and crashed. The
// game's text sprites (every menu and HUD text is a texture of its own, made at run time) were never freed
// either: about 40 each time the pause menu opens. Level.MakeLevel (the rewriter's PatchLevelAssetUnload) calls
// LevelMade first, a frame or more after the old level's GameObjects went: Resources.UnloadUnusedAssets frees what
// nothing uses any more before the new level's bundles are read (a synchronous load waits for it). What the game
// keeps (its sheets in Gfx's tables, its clips in Snd's, the menus' sheets Preload holds) stays loaded.
//   -hunt-unload-assets on|off   on: unused assets unloaded as each level is made (the PS Vita's default; off
//                                elsewhere)
// Each unload logs a HUNT_UNLOAD line with its time.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class LevelAssets
    {
        static bool? on;

        // Level.MakeLevel: a level's load, its reload (RESTART LEVEL) or a boss stage's.
        public static void LevelMade()
        {
            if (on == null) on = HuntFlags.On(ProbeFiles.Args, "-hunt-unload-assets");
            if (!on.Value) return;
            var watch = Stopwatch.StartNew();
            AsyncOperation unload = Resources.UnloadUnusedAssets();
            unload.completed += done => Debug.Log("HUNT_UNLOAD unused assets unloaded in " +
                watch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms");
        }
    }
}
