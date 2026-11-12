using UnityEngine;

// The game's frame rate on the PS Vita: one fixed mode, 60 fps unless the player chose 30. With no saved fps
// setting the game runs in its automatic mode (Main.fpsAuto): 60 fps, down to its 30 fps mode (Main.fps30, its
// logic in 30 steps a second) after about 90 frames under 50 fps, and back to 60 at every ResetFPS, which
// StartMainMenu and each return of the app's focus call. On the PS Vita the menu holds 60 fps but hitches while
// the game loads its level bundles in the background, so the game kept switching between the two modes, and
// froze for a moment at each switch. The rewriter (rewriter/Patches.cs, PatchFrameRate) has Main.Start end with a
// fixed mode where Fixed says so (StartFps: 30 if the player's saved setting is 30, else 60), and Main.SetFPS (the
// options' setting: 60, 30, 0 for automatic) apply Choice's mode instead: automatic becomes the fixed 60. With
// fpsAuto off, neither slow frames nor ResetFPS change the mode. Frames are paced by the display (Display:
// vSyncCount 1 at 60, 2 at 30); the game's own limiter (Main.WaitForNextFrame, at Main.rate) then never waits,
// since a refresh (59.94 Hz) is a little longer than 1/60 s.
//   -hunt-fps 60|30|game   the fixed mode's frame rate on any player; game: the game's automatic mode (the PS
//                          Vita's default is 60, other players' game)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class FrameRate
    {
        static int? fixedFps;  // 0: the game's own automatic mode

        static int FixedFps
        {
            get
            {
                if (fixedFps == null)
                {
                    string value = HuntFlags.Value(ProbeFiles.Args, "-hunt-fps");
                    fixedFps = value == "30" ? 30 : value == "60" ? 60 : value == "game" ? 0
                        : Application.platform == RuntimePlatform.PSP2 ? 60 : 0;
                    if (fixedFps.Value != 0) Debug.Log("HUNT_FRAMERATE fixed mode, " + fixedFps.Value + " fps unless the player chose otherwise");
                }
                return fixedFps.Value;
            }
        }

        public static bool Fixed() { return FixedFps != 0; }

        // Main.Start's mode: the player's saved 30 fps setting (Main.fps30 set from it), else the fixed mode's.
        public static int StartFps(bool savedThirty) { return savedThirty ? 30 : FixedFps; }

        // Main.SetFPS's argument (60, 30, or 0 for automatic) as the mode to apply.
        public static int Choice(int requested) { return requested == 30 || requested == 60 ? requested : FixedFps; }

        public static bool IsThirty(int fps) { return fps == 30; }

        public static float RateOf(int fps) { return 1f / fps; }

        // The display's part of the mode, after the game's fields are set (Patches.cs).
        public static void Display(int fps)
        {
            QualitySettings.vSyncCount = fps == 30 ? 2 : 1;
            Application.targetFrameRate = -1;
            Debug.Log("HUNT_FRAMERATE " + fps + " fps (vSyncCount " + QualitySettings.vSyncCount + ")");
        }
    }
}
