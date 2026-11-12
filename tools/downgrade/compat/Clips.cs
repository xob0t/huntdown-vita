using System.Collections.Generic;
using UnityEngine;

// The game's sound lookups by name (Snd.levelClips["RingoComment1"], every Dictionary<string, AudioClip> indexer
// in its code; rewriter/Patches.cs, RouteClipLookups) go through Get, which logs a name that isn't there
// (HUNT_CLIP_MISSING <name>) and then throws as the dictionary would. The PS Vita's IL2CPP stack traces stop at
// Dictionary.get_Item, so a KeyNotFoundException in the log said nothing of which sound was looked up too early.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Clips
    {
        public static AudioClip Get(Dictionary<string, AudioClip> clips, string name)
        {
            AudioClip clip;
            if (clips.TryGetValue(name, out clip)) return clip;
            Debug.Log("HUNT_CLIP_MISSING " + name + " (" + clips.Count + " clips loaded) at " + Time.realtimeSinceStartup.ToString("F2") + " s");
            return clips[name];
        }
    }
}
