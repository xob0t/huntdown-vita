using UnityEngine;

// The touch screen as the game sees it. Huntdown has touch controls for phones and tablets: a touch switches
// the game to them (Gfx.SetUsesIconography: touch button prompts; MainMenu.InteractTouch: the on-screen stick
// and buttons, which follow the finger). The PS Vita is played with its buttons, so there the game sees no
// touch screen: no touches, and touch unsupported. The rewriter (rewriter/Patches.cs, RouteTouchInput) has
// the game's reads of Input.touchCount and Input.touchSupported go through Count and Supported.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class TouchInput
    {
        public static int Count()
        {
            return HuntFlags.Vita ? 0 : Input.touchCount;
        }

        public static bool Supported()
        {
            return !HuntFlags.Vita && Input.touchSupported;
        }
    }
}
