using System;
using System.IO;
using System.Text;

namespace PlayerMimics;

// Small audio helpers: G.711 mu-law for network transfer (half the size of PCM16), resampling, WAV and base64 PCM16.
internal static class AudioCodec
{
    internal const int NetRate = 16000;
    internal const int ChunkBytes = 4000;

    internal static byte[] MuLawEncode(float[] samples)
    {
        var bytes = new byte[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            int pcm = (int)(Math.Max(-1f, Math.Min(1f, samples[i])) * 32635f);
            int sign = pcm < 0 ? 0x80 : 0;
            if (pcm < 0) pcm = -pcm;
            pcm += 0x84;
            int exponent = 7;
            for (int mask = 0x4000; (pcm & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
            int mantissa = (pcm >> (exponent + 3)) & 0x0F;
            bytes[i] = (byte)~(sign | (exponent << 4) | mantissa);
        }
        return bytes;
    }

    internal static float[] MuLawDecode(byte[] bytes)
    {
        var samples = new float[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            int u = ~bytes[i] & 0xFF;
            int sign = u & 0x80;
            int exponent = (u >> 4) & 0x07;
            int mantissa = u & 0x0F;
            int pcm = ((mantissa << 3) + 0x84) << exponent;
            pcm -= 0x84;
            samples[i] = (sign != 0 ? -pcm : pcm) / 32768f;
        }
        return samples;
    }

    internal static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        int length = (int)((long)input.Length * toRate / fromRate);
        var output = new float[length];
        double step = (double)fromRate / toRate;
        for (int i = 0; i < length; i++)
        {
            double pos = i * step;
            int a = (int)pos;
            int b = Math.Min(a + 1, input.Length - 1);
            float t = (float)(pos - a);
            output[i] = input[a] * (1f - t) + input[b] * t;
        }
        return output;
    }

    internal static string ToPcm16Base64(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(Math.Max(-1f, Math.Min(1f, samples[i])) * 32767f);
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return Convert.ToBase64String(bytes);
    }

    internal static float[] FromPcm16Base64(string b64)
    {
        var bytes = Convert.FromBase64String(b64);
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / 32768f;
        }
        return samples;
    }

    internal static void WriteWav(string path, float[] samples, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (float s in samples) w.Write((short)(Math.Max(-1f, Math.Min(1f, s)) * 32767f));
    }

    // Splits encoded audio into RPC-sized pieces.
    internal static byte[][] Chunk(byte[] data)
    {
        int count = Math.Max(1, (data.Length + ChunkBytes - 1) / ChunkBytes);
        var chunks = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int start = i * ChunkBytes;
            int len = Math.Min(ChunkBytes, data.Length - start);
            chunks[i] = new byte[Math.Max(0, len)];
            if (len > 0) Array.Copy(data, start, chunks[i], 0, len);
        }
        return chunks;
    }
}

// Collects the chunks of one clip in any order.
internal sealed class ChunkAssembly
{
    private readonly byte[][] parts;
    private int received;
    internal readonly float Started;

    internal ChunkAssembly(int count, float now)
    {
        parts = new byte[count][];
        Started = now;
    }

    internal int Count => parts.Length;

    internal bool Add(int index, byte[] data)
    {
        if (index < 0 || index >= parts.Length || parts[index] != null) return false;
        parts[index] = data;
        received++;
        return received == parts.Length;
    }

    internal byte[] Join()
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var all = new byte[total];
        int at = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, all, at, p.Length);
            at += p.Length;
        }
        return all;
    }
}
