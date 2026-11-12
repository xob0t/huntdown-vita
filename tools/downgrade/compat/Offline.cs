using System.Collections;

// The game's analytics: Analytics.LoginWith sends a device ID and the build version to the developer's
// stats server at every start, and once logged in (isOK) the other Analytics methods report game pads,
// levels and deaths there. The port's players never contact it (the PS Vita has no use for it, and test
// players shouldn't report as the game): the rewriter (rewriter/Patches.cs, PatchAnalyticsLogin) makes the
// login coroutines this empty one, so isOK stays false and every report is skipped.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Offline
    {
        public static IEnumerator Nothing()
        {
            yield break;
        }
    }
}
