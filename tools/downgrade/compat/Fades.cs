using System.Collections.Generic;
using UnityEngine;

// Sprites faded all the way out aren't drawn. The game fades its sprites through SpriteObject.SetAlpha and SetFade,
// which only change their colour: one at alpha 0 stays enabled, and the GPU still blends every one of its pixels.
// The main menu's MenuFade, a full-screen sprite, sits at alpha 0 through every level: a whole screen of
// blending each frame for nothing (HUNT_COVER, compat/Diag.cs). Each sprite the game fades is watched (Watch, from
// the start of SetAlpha and SetFade: rewriter/Patches.cs, PatchSpriteFades); just before a camera culls, the
// watched ones at alpha 0 are switched off, and right after it renders they are switched on again, so the game
// never sees a change (and a colour it sets directly still counts).
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Fades
    {
        static readonly HashSet<SpriteRenderer> watched = new HashSet<SpriteRenderer>();
        static readonly List<SpriteRenderer> switchedOff = new List<SpriteRenderer>();
        static readonly List<SpriteRenderer> gone = new List<SpriteRenderer>();
        static bool hooked;

        public static void Watch(SpriteRenderer renderer)
        {
            if (renderer == null || !watched.Add(renderer) || hooked) return;
            Camera.onPreCull += SwitchOff;
            Camera.onPostRender += SwitchOn;
            hooked = true;
        }

        static void SwitchOff(Camera camera)
        {
            foreach (SpriteRenderer renderer in watched)
            {
                if (renderer == null)
                    gone.Add(renderer);  // destroyed
                else if (renderer.enabled && renderer.color.a <= 0f)
                {
                    renderer.enabled = false;
                    switchedOff.Add(renderer);
                }
            }
            foreach (SpriteRenderer renderer in gone) watched.Remove(renderer);
            gone.Clear();
        }

        static void SwitchOn(Camera camera)
        {
            foreach (SpriteRenderer renderer in switchedOff)
                if (renderer != null) renderer.enabled = true;
            switchedOff.Clear();
        }
    }
}
