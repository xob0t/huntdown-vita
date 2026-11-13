using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// How many sprites the game's effect pools keep. On the PS Vita the engine spends about 50 us of the main thread a
// frame on each visible sprite, whatever the game's code does with it, and the frame rate falls under 60 once some
// 300 are on screen. The effects are where most of them come from: in a survey of every level (artifacts/
// gpusurvey.sh), every second under 57 fps that wasn't one long frame had 230-435 sprites visible, most of them
// these: debris (shards, stones, glass) and blood decals lying where they landed, and the weather of a level, all
// on screen at once. Level 2-4 fell to 42-49 fps with its 160 dust motes, 110 pieces of debris and 50 blood decals
// visible; level 4-3 to about 50 with its 80 snowflakes and 120 pieces of debris.
//   debris  FlyingObjects' debris pool (256): an explosion throws as many as before while the pool has room; when
//           it is full the game takes the oldest piece, as at 256 (StartDebris), so old shards go sooner
//   blood   FlyingObjects' blood decals (96): the same, the oldest first
//   rain, snow, ash  a level's weather (FlyingObjects.StartRain 100, StartSnow 80, StartAsh 50): fewer drops,
//           flakes and ash flakes, spread the same
//   dust    area 2's floating dust (Background2_2, 160)
// The rewriter (rewriter/Patches.cs, PatchPoolSizes) has each size go through Size(size, name) where the game sets it.
//   -hunt-pools game|<name>=<n>,...   game: the game's sizes everywhere; or sizes of one's own (never above the
//                                     game's). The PS Vita's defaults are VitaSizes, others' the game's.
// Each pool kept smaller logs a HUNT_POOL line.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Pools
    {
        static readonly Dictionary<string, int> VitaSizes = new Dictionary<string, int>
        {
            { "debris", 96 }, { "blood", 64 }, { "rain", 50 }, { "snow", 40 }, { "ash", 32 }, { "dust", 80 },
        };

        static Dictionary<string, int> chosen;

        public static int Size(int game, string name)
        {
            if (chosen == null) chosen = Choose();
            int size;
            if (!chosen.TryGetValue(name, out size) || size <= 0 || size >= game) return game;
            Debug.Log("HUNT_POOL " + name + ": " + size + " (the game's " + game + ")");
            return size;
        }

        static Dictionary<string, int> Choose()
        {
            string value = HuntFlags.Value(ProbeFiles.Args, "-hunt-pools");
            if (value == "game") return new Dictionary<string, int>();
            var sizes = HuntFlags.Vita ? new Dictionary<string, int>(VitaSizes) : new Dictionary<string, int>();
            if (value == null) return sizes;
            foreach (string pair in value.Split(','))
            {
                string[] parts = pair.Split('=');
                int n;
                if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    sizes[parts[0].Trim()] = n;
                else
                    Debug.Log("HUNT_POOL: " + pair + " is not <name>=<n>");
            }
            return sizes;
        }
    }
}
