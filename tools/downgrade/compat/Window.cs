using System;
using UnityEngine;

// The game's fullscreen and resolution calls (Main's first-run resolution pick, the options' screen
// settings), redirected here by the rewriter (Redirect.cs, ScreenCalls). They do what they did, except
// with -hunt-windowed (run_player.py passes it): then the window stays a window, so a test run never
// takes over the PC's screen, whatever the game's saved display setting says. On the PS Vita they do
// nothing: its screen is 960x544.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Window
    {
        static bool? windowed;
        const int WindowWidth = 960, WindowHeight = 544;  // -hunt-windowed: the PS Vita's screen

        static bool Windowed
        {
            get
            {
                if (windowed == null) windowed = HuntFlags.Has(ProbeFiles.Args, "-hunt-windowed");
                return windowed.Value;
            }
        }

        static bool Fixed { get { return Application.platform == RuntimePlatform.PSP2; } }

        public static void set_fullScreen(bool value)
        {
            if (Fixed) return;
            Screen.fullScreen = value && !Windowed;
        }

        public static void set_fullScreenMode(FullScreenMode value)
        {
            if (Fixed) return;
            Screen.fullScreenMode = Windowed ? FullScreenMode.Windowed : value;
        }

        public static void SetResolution(int width, int height, bool fullscreen)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(WindowWidth, WindowHeight, false);
            else Screen.SetResolution(width, height, fullscreen);
        }

        public static void SetResolution(int width, int height, bool fullscreen, int preferredRefreshRate)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(WindowWidth, WindowHeight, false);
            else Screen.SetResolution(width, height, fullscreen, preferredRefreshRate);
        }

        public static void SetResolution(int width, int height, FullScreenMode mode)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(WindowWidth, WindowHeight, FullScreenMode.Windowed);
            else Screen.SetResolution(width, height, mode);
        }
    }
}
