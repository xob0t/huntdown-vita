using UnityEngine;

// The game's view on the PS Vita: its full 480x270 game pixels at 2 screen pixels each. The game picks its pixel
// scale in Gfx.GetViewSize: ceil(screen height / 270), so that it never shows more than 270 game pixels, which
// suits PC screens (1080 / 270 is 4) but rounds the PS Vita's 544 / 270 = 2.01 up to 3: the game showed 320x181
// of its pixels, zoomed in. Its "maximized view" (the screenMaximized setting) picks the largest scale that
// shows all 480x270 instead, 2 on the PS Vita (960x540, centred). The rewriter (rewriter/Patches.cs,
// PatchViewSize) has GetViewSize take that path where Maximized says so.
//   -hunt-view full|game   full: the maximized view on any player; game: the game's own choice (the PS Vita's
//                          default is full, other players' game)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class View
    {
        static bool? maximized;

        public static bool Maximized()
        {
            if (maximized == null)
            {
                string value = HuntFlags.Value(ProbeFiles.Args, "-hunt-view");
                maximized = value != null ? value == "full" : Application.platform == RuntimePlatform.PSP2;
                if (maximized.Value) Debug.Log("HUNT_VIEW the game's maximized view: all 480x270 game pixels");
            }
            return maximized.Value;
        }
    }
}
