using System.Collections.Generic;
using UnityEngine;

// The opening cutscene's and the menus' sprite sheets, read while the screen is still black. The game loads each
// screen's sheets with Resources.LoadAll<Sprite> as the screen opens, which reads them from the memory card on the
// main thread in that frame. The sheets are uncompressed, as the game ships them
// (asset_rules.texture_compression_overlay): the opening cutscene's Menu/IntroImages is 1 MB, the title's
// Menu/MainMenu 4 MB, the 1 player menu's seven sheets 3.8 MB, and on the PS Vita each stalled one frame for 0.2
// to 1.3 s (stall dumps: Resources.LoadAll from Intro.Init, LogoMenu, ChoosePlayerMenu; compat/LoadTimer.cs's HUNT_LOADTIME lines name them; SerializedFile::ReadObject,
// LocalFileSystemPSP2::Read). At the first rendered frame (the game's startup loads, in Main.Start, are done, and
// the loading splash hasn't begun to fade in), Preload reads them all synchronously and keeps them: the game's own
// LoadAll then finds them in memory and reads nothing. The black screen at startup lasts that much longer.
// Not on the loading thread: the game makes synchronous loads all along (Resources.Load, AssetBundle.LoadFromFile
// for its sound bundles), and each waited for the sheet being read ahead, up to 0.9 s, in the middle of the splash's
// fade, the cutscene or the title. Resources has no asynchronous LoadAll anyway. The sheets are the menus' own, in
// memory once the menus have opened (about 10 MB). No MonoBehaviour and no startup method of its own (Diag.Install
// starts it, Application.onBeforeRender runs it once), so quickcode can ship it.
//   -hunt-preload on|off   on: read the sheets ahead on any player (the PS Vita's default; off elsewhere)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Preload
    {
        // In the order the game opens them: the opening cutscene (Intro.Init), title, main menu, 1 player, game
        // mode, difficulty.
        static readonly string[] Sheets =
        {
            "Menu/IntroImages", "Menu/Black",
            "Menu/MainMenu", "Effects/Ash", "Menu/SelectionSkull", "GUI/Portraits", "GUI/Hud", "GUI/ButtonsPS4",
            "Menu/ChooseBountyHunter", "Players/Mamba/Legs", "Players/Mamba/MainGun", "Players/SawJaw/Legs",
            "Players/SawJaw/MainGun", "Players/MowMan/Legs", "Players/MowMan/MainGun",
            "Menu/GameModeStory", "Menu/GameModeArcade", "Menu/DifficultyOverlay", "Menu/Saving",
        };

        static readonly List<Object[]> kept = new List<Object[]>();
        static bool installed;

        public static void Start(string[] args)
        {
            string value = HuntFlags.Value(args, "-hunt-preload");
            bool on = value != null ? value == "on" : Application.platform == RuntimePlatform.PSP2;
            if (!on || installed) return;
            installed = true;
            Application.onBeforeRender += FirstFrame;
        }

        static void FirstFrame()
        {
            Application.onBeforeRender -= FirstFrame;
            float before = Time.realtimeSinceStartup;
            int sprites = 0;
            foreach (string sheet in Sheets)
            {
                Sprite[] loaded = Resources.LoadAll<Sprite>(sheet);
                kept.Add(loaded);
                sprites += loaded.Length;
            }
            Debug.Log("HUNT_PRELOAD " + Sheets.Length + " sheets, " + sprites + " sprites, read at " + before.ToString("F2") + " s in " +
                      ((Time.realtimeSinceStartup - before) * 1000f).ToString("F0") + " ms");
        }
    }
}
