using System.Collections.Generic;
using UnityEngine;

// A source started with PlayDelayed keeps its delay while paused, as in Unity 2019. A level with an intro to its music
// (1-2, 2-3, 3-3, 4-3, ...) starts it in Snd.StartGameMusic as intro.Play(); loop.PlayDelayed(the intro's length);
// then, the level still loading, loop.Pause(); intro.Pause(), and in LoadLevel about 1.3 s later
// loop.UnPause(); intro.UnPause(). Unity 2018.2's UnPause starts a delayed source that hasn't begun at once: the loop
// played over the intro from the start. The rewriter (rewriter/Patches.cs, PatchDelayedPlay) sends Snd's
// PlayDelayed, Play, Stop, Pause and UnPause on AudioSources here. A source paused while its delay runs is stopped
// instead, with what was left of the delay, and UnPause starts it with PlayDelayed(what was left). Stopped, it reports
// isPlaying false, as a paused source does.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class DelayedPlay
    {
        // Sources whose delay runs: when they start (dspTime). Sources paused during their delay: what was left.
        static readonly Dictionary<AudioSource, double> starts = new Dictionary<AudioSource, double>();
        static readonly Dictionary<AudioSource, double> paused = new Dictionary<AudioSource, double>();

        static void Forget(AudioSource source)
        {
            if (starts.Count > 0) starts.Remove(source);
            if (paused.Count > 0) paused.Remove(source);
        }

        public static void PlayDelayed(AudioSource source, float delay)
        {
            Forget(source);
            source.PlayDelayed(delay);
            if (delay > 0f) starts[source] = AudioSettings.dspTime + delay;
        }

        public static void Play(AudioSource source)
        {
            Forget(source);
            source.Play();
        }

        public static void Stop(AudioSource source)
        {
            Forget(source);
            source.Stop();
        }

        public static void Pause(AudioSource source)
        {
            double start;
            if (starts.TryGetValue(source, out start))
            {
                starts.Remove(source);
                double left = start - AudioSettings.dspTime;
                if (left > 0.0)
                {
                    source.Stop();
                    paused[source] = left;
                    return;
                }
            }
            source.Pause();
        }

        public static void UnPause(AudioSource source)
        {
            double left;
            if (paused.TryGetValue(source, out left))
            {
                paused.Remove(source);
                PlayDelayed(source, (float)left);
                return;
            }
            source.UnPause();
        }
    }
}
