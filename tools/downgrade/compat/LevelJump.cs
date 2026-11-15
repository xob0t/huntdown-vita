using System;
using System.Linq;
using UnityEngine;

// A test flag that loads another level than the one picked, to try any level on a console whose save has only the
// first unlocked. The game starts every level through MainMenu.StartLoadLevel(name), name as in its level files
// ("2-3_misconducts", "1-5a_hoodlum"), which sets the area and level from the name; the rewriter
// (rewriter/Patches.cs, PatchLevelJump) has it start with name = LevelJump.Level(name). Only the first level
// started is replaced, so dying, checkpoints and the next level go on as the game has them.
//   -hunt-level <a>-<l>[a|b]   the level to load instead of the first one picked, as in the bundles'
//                              names (StreamingAssets/levels): 1-1 to 4-5, 1-5a, 2-5a, 3-5a, 4-5a, 4-5b
// A second test flag turns on the game's own cheats (its cheat menu's, GameData's fields) as each level starts,
// so that a test run survives into a level's busiest parts: god (no damage), ammo (unlimited), or a start weapon
// by the game's name (grenadelauncher, howitzer, flamer, minigun, ...).
//   -hunt-cheats <cheat>[,<cheat>...]   e.g. god,ammo,grenadelauncher
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class LevelJump
    {
        static readonly string[] Areas = { "hoodlum", "misconducts", "heatseekers", "no1_suspects", "warlords" };
        static bool used;

        public static string Level(string name)
        {
            Cheats();
            if (used) return name;
            used = true;
            string wanted = HuntFlags.Value(ProbeFiles.Args, "-hunt-level");
            if (wanted == null || wanted.Length < 3 || wanted[1] != '-' || wanted[0] < '1' || wanted[0] > '5') return name;
            string level = wanted + "_" + Areas[wanted[0] - '1'];
            Debug.Log("HUNT_LEVEL " + level + " instead of " + name);
            return level;
        }

        static void Cheats()
        {
            string wanted = HuntFlags.Value(ProbeFiles.Args, "-hunt-cheats");
            if (wanted == null) return;
            Type mainType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Main", false)).FirstOrDefault(t => t != null);
            UnityEngine.Object main = mainType == null ? null : UnityEngine.Object.FindObjectOfType(mainType);
            object data = main == null ? null : mainType.GetField("gameData").GetValue(main);
            if (data == null) return;
            foreach (string cheat in wanted.Split(','))
            {
                if (cheat == "god") data.GetType().GetField("godMode").SetValue(data, true);
                else if (cheat == "ammo") data.GetType().GetField("unlimitedAmmo").SetValue(data, true);
                else data.GetType().GetField("startWeapon").SetValue(data, cheat);
            }
            Debug.Log("HUNT_CHEATS " + wanted);
        }
    }
}
