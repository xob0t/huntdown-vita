using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Audio;

// -hunt-audio-log <s>: every s seconds, the game's sound manager (Snd) as the logs can show it: its music, intro,
// ambience and first player's weapon sources (clip, playing, time, volume, loop), its music state, the mixer's
// exposed volumes as the game sets them, and how many sources play and how many of those are virtual (culled).
// Lines start "HUNT_AUDIO". (AudioSource.GetOutputData reads only zeros on the PS Vita: no output levels.)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public class AudioLog : MonoBehaviour
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        static readonly string[] Sources = { "musicSource", "musicIntroSource", "ambienceSource", "p1Source1", "p1Source2" };
        static readonly string[] States = { "musicStatus", "p1Looping", "p1Loop", "p1Weapon" };
        static readonly string[] Volumes = { "effectVolume", "musicVolume", "menuVolume" };
        static readonly PropertyInfo IsVirtual = typeof(AudioSource).GetProperty("isVirtual");

        float every, last;

        MonoBehaviour snd;

        internal static void Install(string[] args)
        {
            float every = HuntFlags.Number(args, "-hunt-audio-log", -1f);
            if (every <= 0f) return;
            var probe = new GameObject("HuntAudioLog");
            DontDestroyOnLoad(probe);
            probe.AddComponent<AudioLog>().every = every;
            AudioConfiguration config = AudioSettings.GetConfiguration();
            Debug.Log("HUNT_AUDIO config realVoices=" + config.numRealVoices + " virtualVoices=" + config.numVirtualVoices +
                      " dspBuffer=" + config.dspBufferSize + " rate=" + config.sampleRate + " isVirtual=" + (IsVirtual != null));
        }

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now - last < every) return;
            last = now;
            if (snd == null) snd = FindObjectsOfType<MonoBehaviour>().FirstOrDefault(b => b.GetType().Name == "Snd");
            var line = new StringBuilder("HUNT_AUDIO t=").Append(now.ToString("F2", Inv));
            line.Append(" dsp=").Append(AudioSettings.dspTime.ToString("F2", Inv));
            if (snd != null)
            {
                Type type = snd.GetType();
                foreach (string name in States)
                {
                    FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    line.Append(' ').Append(name).Append('=').Append(field == null ? "?" : Convert.ToString(field.GetValue(snd), Inv) ?? "null");
                }
                foreach (string name in Sources)
                {
                    FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var source = field == null ? null : field.GetValue(snd) as AudioSource;
                    line.Append(' ').Append(name).Append('=');
                    if (source == null) { line.Append("none"); continue; }
                    line.Append(source.clip == null ? "noclip" : source.clip.name.Replace(' ', '_'))
                        .Append(source.isPlaying ? ",play" : ",stop").Append(source.enabled ? "" : ",off")
                        .Append(',').Append(source.time.ToString("F2", Inv))
                        .Append(",v").Append(source.volume.ToString("F2", Inv))
                        .Append(source.loop ? ",loop" : "");
                    if (IsVirtual != null && (bool)IsVirtual.GetValue(source, null)) line.Append(",virtual");
                }
                FieldInfo mixerField = type.GetField("masterMixer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var mixer = mixerField == null ? null : mixerField.GetValue(snd) as AudioMixer;
                if (mixer != null)
                    foreach (string name in Volumes)
                    {
                        float value;
                        line.Append(' ').Append(name).Append('=').Append(mixer.GetFloat(name, out value) ? value.ToString("F1", Inv) : "missing");
                    }
            }
            int playing = 0, culled = 0;
            foreach (AudioSource source in FindObjectsOfType<AudioSource>())
            {
                if (!source.isPlaying) continue;
                playing++;
                if (IsVirtual != null && (bool)IsVirtual.GetValue(source, null)) culled++;
            }
            line.Append(" playing=").Append(playing).Append(" virtual=").Append(culled);
            Debug.Log(line.ToString());
        }
    }
}
