using System.Linq;
using UnityEngine;

// The game's menus on the PS Vita, without what a PS Vita doesn't have: a second player, a screen resolution and a
// desktop to quit to.
// The rewriter (rewriter/Patches.cs, PatchMenus) calls these:
// - Player two. GamePad.GetPlayerHasController and SearchForPlayerController say no for the second player
//   (PlayerTwo: false), so no pad is taken for player two and no second pad's buttons join, pause or steer the
//   menus; MainMenu.PromptPlayerTwo returns at once, so the "press ... to join" prompt is never made (and without
//   it the menus don't offer to join either); the main menu (StartMenu.Init) leaves out 2 PLAYERS and the pause
//   menus (PauseMenu.Init and MapPauseMenu.Init) JOIN PLAYER 2: its index (TwoPlayerEntry) is -1, the game's own
//   "not in this menu", and the entries after it move up (TwoPlayerEntries: 0). Without 2 PLAYERS the main menu's
//   first entry is the game's own START GAME instead of 1 PLAYER.
// - Quitting. The main menu's EXIT GAME (StartMenu.Init) and the pause menu's quit menu's EXIT TO DESKTOP
//   (AbandonGameMenu.Init) go the same way (QuitEntry, QuitEntries): a PS Vita game is closed from the system
//   (the PS button), not from its own menus.
// - The resolution. Options > Video's first entry, RESOLUTION, opens the game's resolution and full-screen menu;
//   on the PS Vita the screen is always 960x544. VideoMenu.Init leaves it out of its entries (VideoChoices), and
//   since the menu's code names its entries by fixed index (0 resolution, 1 CRT filter, 2 vsync, 3 frame rate,
//   4 defaults, 5 back), VideoMenu's reads of the selected index go through VideoIndex (the shown index to the
//   game's) and its constant indexes into the entries (menuChoices[2], UpdateSelection(2)) through VideoShown
//   (the game's to the shown one).
//   -hunt-menus vita|game   vita: these menus on any player; game: the game's own (the PS Vita's default is vita,
//                           other players' game)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Menus
    {
        static bool? trimmed;

        static bool Trimmed
        {
            get
            {
                if (trimmed == null)
                {
                    trimmed = HuntFlags.On(ProbeFiles.Args, "-hunt-menus", "vita");
                    if (trimmed.Value) Debug.Log("HUNT_MENUS no player two, no resolution menu and no quit");
                }
                return trimmed.Value;
            }
        }

        // Whether the game's player (0 the first, 1 the second) may have a pad.
        public static bool PlayerTwo(int player) { return player == 0 || !Trimmed; }

        public static bool PromptPlayerTwo() { return !Trimmed; }

        public static int TwoPlayerEntries() { return Trimmed ? 0 : 1; }

        public static int TwoPlayerEntry(int index) { return Trimmed ? -1 : index; }

        public static int QuitEntries() { return Trimmed ? 0 : 1; }

        public static int QuitEntry(int index) { return Trimmed ? -1 : index; }

        const int Resolution = 0;

        public static string[] VideoChoices(string[] choices)
        {
            return Trimmed ? choices.Where((choice, i) => i != Resolution).ToArray() : choices;
        }

        public static int VideoIndex(int shown) { return Trimmed && shown >= Resolution ? shown + 1 : shown; }

        public static int VideoShown(int index) { return Trimmed && index > Resolution ? index - 1 : index; }
    }
}
