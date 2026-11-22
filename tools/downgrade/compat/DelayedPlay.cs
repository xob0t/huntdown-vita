using System.Collections.Generic;
using UnityEngine;

// A source started with PlayDelayed keeps its delay while paused, and a source paused in the frame it was started
// stays at its start, as in Unity 2019. A level with an intro to its music (1-2, 2-3, 3-3, 4-3, ...) starts it in
// Snd.StartGameMusic as intro.Play(); loop.PlayDelayed(the intro's length); then, the level still loading,
// loop.Pause(); intro.Pause(), and in LoadLevel about 1.3 s later loop.UnPause(); intro.UnPause(). In Unity 2018.2 on
// the PS Vita, UnPause starts a delayed source that hasn't begun at once, so the loop played over the intro from the
// start; and the intro's Pause, in the frame of its Play, did nothing, so the intro ran on through the pause. The
// rewriter (rewriter/Patches.cs, PatchDelayedPlay) sends Snd's PlayDelayed, Play, Stop, Pause and UnPause on
// AudioSources here. A source paused while its delay runs, or in the frame it was started, is stopped instead, with
// what was left of the delay, and UnPause starts it again with that delay: intro and loop start together from the
// unpause, the loop the intro's length later. Stopped, it reports isPlaying false, as a paused source does.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class DelayedPlay
    {
        // Sources whose delay runs: when they start (dspTime). Sources paused during their delay: what was left.
        // Sources started: the frame.
        static readonly Dictionary<AudioSource, double> starts = new Dictionary<AudioSource, double>();
        static readonly Dictionary<AudioSource, int> startFrames = new Dictionary<AudioSource, int>();
        static readonly Dictionary<AudioSource, double> paused = new Dictionary<AudioSource, double>();

        static void Forget(AudioSource source)
        {
            if (starts.Count > 0) starts.Remove(source);
            if (paused.Count > 0) paused.Remove(source);
            if (startFrames.Count > 0) startFrames.Remove(source);
        }

        public static void PlayDelayed(AudioSource source, float delay)
        {
            Forget(source);
            if (delay > 0f) source.PlayDelayed(delay);
            else source.Play();
            starts[source] = AudioSettings.dspTime + delay;
            startFrames[source] = Time.frameCount;
        }

        public static void Play(AudioSource source)
        {
            PlayDelayed(source, 0f);
        }

        public static void Stop(AudioSource source)
        {
            Forget(source);
            source.Stop();
        }

        public static void Pause(AudioSource source)
        {
            double start;
            int frame;
            if (starts.TryGetValue(source, out start) && startFrames.TryGetValue(source, out frame))
            {
                double left = start - AudioSettings.dspTime;
                Forget(source);
                if (left > 0.0 || frame == Time.frameCount)
                {
                    source.Stop();
                    paused[source] = System.Math.Max(left, 0.0);
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
