using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

// The mixer's volumes, applied to the sources themselves on the PS Vita. The game sets its sound and music volumes
// and mutes them in the pause menu with MasterMixer's exposed parameters (Snd.SetMuteEffects, SetMuteMusic,
// SetBaseSoundVolume: effectVolume, the EffectGroups group's volume, over Effects, Voices and NoDuck; musicVolume,
// Music's; menuVolume, Menu's), and every source goes to one of those groups (Snd.Init). On the PS Vita the
// mixer takes the values (GetFloat reads them back) but nothing is heard of them: the music played on in the pause
// menu, a weapon's loop went on at full volume behind it, and Options' Sound volume at 0 left the gunfire as loud.
// The rewriter (rewriter/Patches.cs, PatchMixVolume) sends the game's AudioMixer.SetFloat and its AudioSource
// volume, outputAudioMixerGroup and Play calls here. A source's volume is what the game set times its group's
// gain (0 at -80 dB); reading it gives what the game set. When a gain changes, every source the game set the
// volume of and every active one is set again; a source the game never set is taken at its volume when it plays
// or when a gain changes (its volume is then still the game's). The mixer gets the values too, as before.
//   -hunt-mix-volume on|off   on: the volumes on the sources (the PS Vita's default; off elsewhere, where the
//                             mixer applies them itself)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class MixVolume
    {
        // The gain kinds: none (Master, no group), effects (EffectGroups and its children), music, menu.
        const int None = 0, Effects = 1, Music = 2, Menu = 3;
        static readonly float[] gains = { 1f, 1f, 1f, 1f };
        static bool allOne = true;

        struct Entry
        {
            public float volume;  // what the game set
            public int kind;
        }

        static readonly Dictionary<AudioSource, Entry> sources = new Dictionary<AudioSource, Entry>();
        static int pruneAt = 256;
        static bool? on;

        static bool On
        {
            get
            {
                if (on == null) on = HuntFlags.On(ProbeFiles.Args, "-hunt-mix-volume");
                return on.Value;
            }
        }

        static int KindOf(AudioMixerGroup group)
        {
            if (group == null) return None;
            switch (group.name)
            {
                case "EffectGroups": case "Effects": case "Voices": case "NoDuck": return Effects;
                case "Music": return Music;
                case "Menu": return Menu;
                default: return None;
            }
        }

        static int KindOf(string parameter)
        {
            switch (parameter)
            {
                case "effectVolume": return Effects;
                case "musicVolume": return Music;
                case "menuVolume": return Menu;
                default: return -1;
            }
        }

        // The game's volume of a source taken now, if it isn't known yet; its volume set from it.
        static void Track(AudioSource source, float volume)
        {
            Entry entry;
            if (!sources.TryGetValue(source, out entry))
            {
                if (sources.Count >= pruneAt) Prune();
                entry.kind = KindOf(source.outputAudioMixerGroup);
            }
            entry.volume = volume;
            sources[source] = entry;
            source.volume = volume * gains[entry.kind];
        }

        static void Prune()
        {
            var gone = new List<AudioSource>();
            foreach (AudioSource source in sources.Keys)
                if (source == null) gone.Add(source);
            foreach (AudioSource source in gone) sources.Remove(source);
            pruneAt = Mathf.Max(256, sources.Count * 2);
        }

        static void ApplyAll()
        {
            Prune();
            var known = new List<AudioSource>(sources.Keys);
            foreach (AudioSource source in known) Track(source, sources[source].volume);
            foreach (AudioSource source in Object.FindObjectsOfType<AudioSource>())
                if (!sources.ContainsKey(source)) Track(source, source.volume);
        }

        // Before a source plays: a source the game never set the volume of gets its group's gain.
        public static void Prepare(AudioSource source)
        {
            if (allOne || !On || sources.ContainsKey(source)) return;
            Track(source, source.volume);
        }

        public static bool SetFloat(AudioMixer mixer, string name, float value)
        {
            bool result = mixer.SetFloat(name, value);
            int kind = KindOf(name);
            if (kind < 0 || !On) return result;
            float gain = value <= -80f ? 0f : Mathf.Pow(10f, value / 20f);
            if (gain == gains[kind]) return result;
            gains[kind] = gain;
            allOne = gains[Effects] == 1f && gains[Music] == 1f && gains[Menu] == 1f;
            ApplyAll();
            return result;
        }

        public static void SetVolume(AudioSource source, float volume)
        {
            if (On) Track(source, volume);
            else source.volume = volume;
        }

        public static float GetVolume(AudioSource source)
        {
            Entry entry;
            if (sources.Count > 0 && sources.TryGetValue(source, out entry)) return entry.volume;
            return source.volume;
        }

        public static void SetOutputGroup(AudioSource source, AudioMixerGroup group)
        {
            source.outputAudioMixerGroup = group;
            Entry entry;
            if (!On || !sources.TryGetValue(source, out entry)) return;
            entry.kind = KindOf(group);
            sources[source] = entry;
            source.volume = entry.volume * gains[entry.kind];
        }

        public static void Play(AudioSource source)
        {
            Prepare(source);
            source.Play();
        }

        public static void PlayDelayed(AudioSource source, float delay)
        {
            Prepare(source);
            source.PlayDelayed(delay);
        }

        public static void PlayScheduled(AudioSource source, double time)
        {
            Prepare(source);
            source.PlayScheduled(time);
        }

        public static void PlayOneShot(AudioSource source, AudioClip clip)
        {
            Prepare(source);
            source.PlayOneShot(clip);
        }

        public static void PlayOneShotScaled(AudioSource source, AudioClip clip, float volumeScale)
        {
            Prepare(source);
            source.PlayOneShot(clip, volumeScale);
        }

        public static void UnPause(AudioSource source)
        {
            Prepare(source);
            source.UnPause();
        }
    }
}
