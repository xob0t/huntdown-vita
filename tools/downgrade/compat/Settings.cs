using System;
using System.Globalization;
using System.Threading;
using UnityEngine;

// The culture numbers and dates are written and read in. The PS Vita's system language would give the
// game's text parsing a culture of its own (a decimal comma, say); -hunt-culture <name|invariant|system>
// chooses, and the PS Vita's default is the invariant culture. Set from ProbeFiles.Install (BeforeSceneLoad,
// before the first scene): DefaultThreadCurrentCulture and the main thread's own, so threads started before
// follow it too.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Settings
    {
        static bool Vita { get { return Application.platform == RuntimePlatform.PSP2; } }

        // The line to log (null on desktop players left at the system's). Never throws: a name the runtime
        // doesn't know (CultureNotFoundException) leaves the system's, with a "failed" line.
        internal static string Culture()
        {
            string value = "?", system = "?";
            try
            {
                system = CultureInfo.CurrentCulture.Name;
                if (system.Length == 0) system = "invariant";
                string given = HuntFlags.Value(ProbeFiles.Args, "-hunt-culture");
                value = given ?? (Vita ? "invariant" : "system");
                if (value == "system") return given != null || Vita ? "HUNT_SETTINGS culture system (" + system + "; 1.5 is " + 1.5f + ")" : null;
                CultureInfo culture = value == "invariant" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(value);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                return "HUNT_SETTINGS culture " + value + " (the system's: " + system + "; 1.5 is " + 1.5f + ")";
            }
            catch (Exception e)  // nothing was set: GetCultureInfo throws before the setters
            {
                return "HUNT_SETTINGS culture " + value + " failed (" + e.GetType().Name + ": " + e.Message.Replace("\r", "").Replace('\n', ' ') + "), the system's (" + system + ") kept";
            }
        }
    }
}
