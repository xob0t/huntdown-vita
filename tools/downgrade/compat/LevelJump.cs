using UnityEngine;

// A test flag that loads another level than the one picked, to try any level on a console whose save has only the
// first unlocked. The game starts every level through MainMenu.StartLoadLevel(name), name as in its level files
// ("2-3_misconducts", "1-5a_hoodlum"), which sets the area and level from the name; the rewriter
// (rewriter/Patches.cs, PatchLevelJump) has it start with name = LevelJump.Level(name). Only the first level
// started is replaced, so dying, checkpoints and the next level go on as the game has them.
//   -hunt-level <a>-<l>[a|b]   the level to load instead of the first one picked, as in the bundles'
//                              names (StreamingAssets/levels): 1-1 to 4-5, 1-5a, 2-5a, 3-5a, 4-5a, 4-5b
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class LevelJump
    {
        static readonly string[] Areas = { "hoodlum", "misconducts", "heatseekers", "no1_suspects", "warlords" };
        static bool used;

        public static string Level(string name)
        {
            if (used) return name;
            used = true;
            string wanted = HuntFlags.Value(ProbeFiles.Args, "-hunt-level");
            if (wanted == null || wanted.Length < 3 || wanted[1] != '-' || wanted[0] < '1' || wanted[0] > '5') return name;
            string level = wanted + "_" + Areas[wanted[0] - '1'];
            Debug.Log("HUNT_LEVEL " + level + " instead of " + name);
            return level;
        }
    }
}
