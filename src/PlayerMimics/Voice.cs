using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dissonance;
using Dissonance.Audio.Capture;
using NAudio.Wave;
using UnityEngine;

namespace PlayerMimics;

// Runs on every player. Only while they consented and the host wants voice (its AI server is up, or it plays back
// random clips without one), it cuts the voice chat they are already transmitting into utterances and sends them to
// the host. Nothing is kept on this computer.
internal sealed class VoiceCapture : MonoBehaviour, IMicrophoneSubscriber
{
    private const float FrameSeconds = 0.02f;
    private const int OpenFrames = 3;        // 60 ms of speech starts an utterance
    private const int CloseFrames = 30;      // 600 ms of quiet ends it
    private const int LeadFrames = 8;        // keep 160 ms before the start
    private const float MaxSeconds = 12f;
    private const float MinSeconds = 0.8f;

    private DissonanceComms? comms;
    private volatile bool capturing;
    private volatile int rate;
    private volatile int channels;
    private readonly ConcurrentQueue<float[]> blocks = new();

    private readonly List<float> pending = new();
    private readonly Queue<float[]> lead = new();
    private readonly List<float> speech = new();
    private bool speaking;
    private int loudRun;
    private int quietRun;
    private float noiseFloor = -60f;
    private int utteranceId;

    private bool ShouldCapture()
    {
        var net = ModNetwork.Instance;
        if (net == null || !ModNetwork.HostStatusKnown || !ModNetwork.HostWantsVoice) return false;
        if (Cfg.VoiceConsent.Value != ConsentChoice.Allow || !Cfg.ConsentIncludesReplay.Value) return false;
        if (ModNetwork.HostSavesAudio && !Cfg.ConsentIncludedSaving.Value) return false; // they haven't agreed to that yet
        if (ModNetwork.HostCollectsText && !Cfg.ConsentIncludedTraining.Value) return false;
        if (comms == null || comms.IsMuted) return false;
        var settings = IngamePlayerSettings.Instance;
        return settings != null && settings.settings.micEnabled;
    }

    private void Update()
    {
        if (comms == null)
        {
            var round = StartOfRound.Instance;
            if (round == null || round.voiceChatModule == null) return;
            comms = round.voiceChatModule;
            comms.SubscribeToRecordedAudio(this);
        }
        bool should = ShouldCapture();
        if (!should && capturing)
        {
            Finish();
            ResetDetector();
        }
        capturing = should;
        while (blocks.TryDequeue(out var block))
        {
            if (capturing) Process(block);
        }
    }

    private void OnDestroy()
    {
        capturing = false;
        try
        {
            comms?.UnsubscribeFromRecordedAudio(this);
        }
        catch (Exception)
        {
            // The comms object is already gone when the game shuts down.
        }
    }

    // Dissonance calls this from its audio thread with the processed mic signal.
    void IMicrophoneSubscriber.ReceiveMicrophoneData(ArraySegment<float> data, WaveFormat format)
    {
        if (!capturing || data.Array == null) return;
        rate = format.SampleRate;
        channels = format.Channels;
        var copy = new float[data.Count];
        Array.Copy(data.Array, data.Offset, copy, 0, data.Count);
        blocks.Enqueue(copy);
    }

    void IMicrophoneSubscriber.Reset()
    {
    }

    private void Process(float[] block)
    {
        int r = rate;
        int ch = Math.Max(1, channels);
        if (r <= 0) return;
        for (int i = 0; i + ch - 1 < block.Length; i += ch)
        {
            float sum = 0f;
            for (int c = 0; c < ch; c++) sum += block[i + c];
            pending.Add(sum / ch);
        }
        int frame = Math.Max(1, (int)(r * FrameSeconds));
        int used = 0;
        while (pending.Count - used >= frame)
        {
            Frame(pending.GetRange(used, frame).ToArray(), r);
            used += frame;
        }
        pending.RemoveRange(0, used);
    }

    private void Frame(float[] frame, int r)
    {
        double energy = 0;
        foreach (float s in frame) energy += s * s;
        float db = 10f * Mathf.Log10((float)(energy / frame.Length) + 1e-12f);
        noiseFloor = db < noiseFloor ? Mathf.Lerp(noiseFloor, db, 0.25f) : noiseFloor + 0.01f;
        bool loud = db > Mathf.Max(-50f, noiseFloor + 10f);

        if (!speaking)
        {
            lead.Enqueue(frame);
            while (lead.Count > LeadFrames) lead.Dequeue();
            loudRun = loud ? loudRun + 1 : 0;
            if (loudRun < OpenFrames) return;
            speaking = true;
            quietRun = 0;
            speech.Clear();
            foreach (var f in lead) speech.AddRange(f);
            lead.Clear();
            return;
        }
        speech.AddRange(frame);
        quietRun = loud ? 0 : quietRun + 1;
        if (quietRun >= CloseFrames || speech.Count >= MaxSeconds * r)
        {
            Finish();
            ResetDetector();
        }
    }

    private void Finish()
    {
        int r = rate;
        if (!speaking || r <= 0)
        {
            speech.Clear();
            return;
        }
        int trailing = Math.Min(speech.Count, Math.Max(0, quietRun - 8) * (int)(r * FrameSeconds));
        int keep = speech.Count - trailing;
        if (keep >= MinSeconds * r)
        {
            Send(AudioCodec.Resample(speech.GetRange(0, keep).ToArray(), r, AudioCodec.NetRate));
        }
        speech.Clear();
    }

    private void ResetDetector()
    {
        speaking = false;
        loudRun = 0;
        quietRun = 0;
        lead.Clear();
    }

    private void Send(float[] samples)
    {
        var net = ModNetwork.Instance;
        if (net == null) return;
        if (net.IsServer)
        {
            // The host's own voice doesn't need to travel.
            var me = GameNetworkManager.Instance?.localPlayerController;
            if (me != null && HostServices.Instance != null && HostServices.Instance.HasConsent(me))
            {
                HostServices.Instance.Ingest(me, samples, AudioCodec.NetRate);
            }
            return;
        }
        var chunks = AudioCodec.Chunk(AudioCodec.MuLawEncode(samples));
        int id = utteranceId++;
        for (int i = 0; i < chunks.Length; i++)
        {
            net.VoiceChunkServerRpc(id, i, chunks.Length, AudioCodec.NetRate, chunks[i]);
        }
    }
}

// Plays a Masked's cloned lines from its head, routed and filtered like the copied player's voice chat.
internal sealed class MimicVoice : MonoBehaviour
{
    private const int MaxLineSeconds = 60;

    private AudioSource? source;
    private AudioClip? clip;

    // The line being received. Pieces are appended on the main thread and read by Unity's audio thread
    // (OnAudioRead), so the queue and counters are shared under 'gate'.
    private readonly object gate = new();
    private readonly Queue<float[]> queue = new();
    private int headPos;          // read position in queue.Peek()
    private int written;          // samples handed to Unity so far, gap silence included
    private int realEnd;          // 'written' at the end of the last real sample
    private bool lineFinal;
    private int currentLine = -1;
    private int lastSeq = -1;
    private int lineRate = AudioCodec.NetRate;
    private bool playing;

    internal bool Speaking => playing || (source != null && source.isPlaying);

    internal void ReceiveStream(int line, int seq, int rate, byte[] data, bool start, bool final)
    {
        var masked = GetComponent<MaskedPlayerEnemy>();
        if (masked == null || masked.isEnemyDead) return;
        if (line != currentLine)
        {
            if (line < currentLine) return; // a piece of an older line
            StopLine();                     // a new line cuts off the old one
            currentLine = line;
            lastSeq = -1;
            lineRate = rate;
        }
        if (seq <= lastSeq) return;
        lastSeq = seq;
        lock (gate)
        {
            if (data.Length > 0) queue.Enqueue(AudioCodec.MuLawDecode(data));
            if (final) lineFinal = true;
        }
        if (start && !playing)
        {
            StartPlaying(masked);
        }
        else if (final && !playing)
        {
            StopLine(); // ended without ever starting (the host gave up on it)
        }
    }

    private void StartPlaying(MaskedPlayerEnemy masked)
    {
        Ensure(masked);
        source!.Stop();
        if (clip != null) Destroy(clip);
        lock (gate)
        {
            written = 0;
            realEnd = 0;
        }
        // A streamed clip pulls its samples from OnAudioRead as it plays, so it can start before the line is complete.
        clip = AudioClip.Create("PlayerMimicsLine", lineRate * MaxLineSeconds, 1, lineRate, true, OnAudioRead);
        Route(MimicDisguise.Of(masked)?.Player);
        source.clip = clip;
        source.loop = false;
        source.volume = Cfg.MimicVoiceVolume.Value;
        source.Play();
        playing = true;
    }

    // Unity's audio thread. Runs out of audio mid-line -> silence until more arrives.
    private void OnAudioRead(float[] data)
    {
        lock (gate)
        {
            int i = 0;
            while (i < data.Length && queue.Count > 0)
            {
                var head = queue.Peek();
                int n = Math.Min(data.Length - i, head.Length - headPos);
                Array.Copy(head, headPos, data, i, n);
                i += n;
                headPos += n;
                if (headPos >= head.Length)
                {
                    queue.Dequeue();
                    headPos = 0;
                }
                realEnd = written + i;
            }
            if (i < data.Length) Array.Clear(data, i, data.Length - i);
            written += data.Length;
        }
    }

    private void Update()
    {
        if (!playing || source == null) return;
        bool finished;
        lock (gate)
        {
            finished = lineFinal && queue.Count == 0 && source.timeSamples >= realEnd;
        }
        if (finished || !source.isPlaying) StopLine();
    }

    private void StopLine()
    {
        playing = false;
        if (source != null) source.Stop();
        if (clip != null)
        {
            Destroy(clip);
            clip = null;
        }
        lock (gate)
        {
            queue.Clear();
            headPos = 0;
            lineFinal = false;
        }
    }

    internal static void Fade(float[] s, int n)
    {
        n = Math.Min(n, s.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float g = (float)i / n;
            s[i] *= g;
            s[s.Length - 1 - i] *= g;
        }
    }

    private void Ensure(MaskedPlayerEnemy masked)
    {
        if (source != null) return;
        var go = new GameObject("PlayerMimicsVoice");
        go.transform.SetParent(masked.eye != null ? masked.eye : transform, false);
        source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 2f;
        source.maxDistance = 50f;
        go.AddComponent<AudioLowPassFilter>().cutoffFrequency = 20000f;
        go.AddComponent<OccludeAudio>();
    }

    // Same mixer (their volume slider) and falloff as the real player's voice.
    private void Route(GameNetcodeStuff.PlayerControllerB? player)
    {
        if (player == null || source == null) return;
        var sound = SoundManager.Instance;
        if (sound != null && sound.playerVoiceMixers != null && player.playerClientId < (ulong)sound.playerVoiceMixers.Length)
        {
            source.outputAudioMixerGroup = sound.playerVoiceMixers[player.playerClientId];
        }
        var voice = player.currentVoiceChatAudioSource;
        if (voice == null) return;
        source.rolloffMode = voice.rolloffMode;
        source.minDistance = voice.minDistance;
        source.maxDistance = voice.maxDistance;
        if (voice.rolloffMode == AudioRolloffMode.Custom)
        {
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, voice.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
        }
    }

    private void OnDestroy()
    {
        if (clip != null) Destroy(clip);
    }
}
