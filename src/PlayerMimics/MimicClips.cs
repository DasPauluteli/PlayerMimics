using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PlayerMimics;

// Host only. Without the AI server, a Masked plays back things its player really said on voice chat, now and then,
// like Mirage. The recordings are the same consented utterances the server would get; they stay in memory and are
// gone when the game closes, when the player withdraws consent, or when a new session starts.
internal static class MimicClips
{
    private const int PerPlayer = 30;
    private const float MinSeconds = 0.6f, MaxSeconds = 8f;
    private const float MinAge = 30f;    // just said it: the same people would hear it twice in a row
    private const float ReplayGap = 90f; // the same recording again soon is a giveaway

    private sealed class Clip
    {
        internal float[] Samples = null!;
        internal float RecordedAt;
        internal float PlayedAt = -999f;
    }

    private static readonly Dictionary<string, List<Clip>> clips = new();

    internal static void Add(string key, float[] samples, int rate)
    {
        float seconds = samples.Length / (float)rate;
        if (seconds < MinSeconds || seconds > MaxSeconds) return;
        if (!clips.TryGetValue(key, out var list)) clips[key] = list = new List<Clip>();
        list.Add(new Clip { Samples = AudioCodec.Resample(samples, rate, AudioCodec.NetRate), RecordedAt = Time.time });
        if (list.Count > PerPlayer) list.RemoveAt(0);
    }

    // A random recording that's fine to play now, at AudioCodec.NetRate, or null.
    internal static float[]? Pick(string key)
    {
        if (!clips.TryGetValue(key, out var list)) return null;
        float now = Time.time;
        var ok = list.Where(c => now - c.RecordedAt >= MinAge && now - c.PlayedAt >= ReplayGap).ToList();
        if (ok.Count == 0) return null;
        var clip = ok[Random.Range(0, ok.Count)];
        clip.PlayedAt = now;
        return (float[])clip.Samples.Clone();
    }

    internal static void Forget(string key) => clips.Remove(key);

    internal static void Clear() => clips.Clear();
}
