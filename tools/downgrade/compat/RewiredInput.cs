using System.Linq;
using UnityEngine;

// Which Unity joystick slots Rewired reads axes from. The game ships Rewired's Windows plugin only, so on the PS Vita
// Rewired reads its controllers through Unity's Input: each frame, every axis of every joystick slot Unity reports
// (four on the PS Vita, the pad in one of them), each an Input.GetAxis by name ("Joy3Axis17") that Unity looks up
// among the game's 552 axes (ProjectSettings/InputManager.asset: Rewired's 16 joysticks of 29 axes and the game's own).
// In a HUNT_PROF=1 build (compat/Prof.cs) that was 1.7 ms of each frame, the most of Rewired's 3.5 ms.
// The rewriter (rewriter/Patches.cs, SkipEmptyJoystickAxes) starts Rewired's UnityInputHelper axis readers with
// Polled(joystick, axis): a slot whose name Unity reports empty reads 0 without a lookup, and so does an axis past the
// 8th: Rewired's PS Vita map (SonyPlaystationVita.asset) reads axes 1, 2, 4 and 5, the sticks, and 6 and 7, but
// Rewired reads all 29 of a pad to see whether any moves. The names are read again every 120 frames, so a pad that
// comes or goes (a PS TV's) is picked up as Rewired itself picks it up. Measured in level 2-4, Rewired went from
// 3.3 ms of each frame to 1.5 with this and its keyboard and mouse off (compat/RewiredTrim.cs).
//   -hunt-rewired-axes all   read every slot, as the game does (the default off the PS Vita)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class RewiredInput
    {
        static bool? everySlot;
        static string[] names;
        static int namesFrame;

        const int VitaAxes = 8;

        public static bool Polled(int joystick, int axis)
        {
            if (everySlot == null) everySlot = !HuntFlags.Vita || HuntFlags.Value(ProbeFiles.Args, "-hunt-rewired-axes") == "all";
            if (everySlot.Value) return true;
            if (names == null || Time.frameCount - namesFrame >= 120)
            {
                string[] now = Input.GetJoystickNames();
                if (names == null || !now.SequenceEqual(names))
                    Debug.Log("HUNT_REWIRED_INPUT joystick slots: " + string.Join(", ", now.Select((n, i) => (i + 1) + "=\"" + n + "\"").ToArray()));
                names = now;
                namesFrame = Time.frameCount;
            }
            return axis < VitaAxes && joystick >= 1 && joystick <= names.Length && !string.IsNullOrEmpty(names[joystick - 1]);
        }
    }
}
