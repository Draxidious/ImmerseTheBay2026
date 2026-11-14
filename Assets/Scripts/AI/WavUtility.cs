using System;
using System.IO;
using System.Text;

public static class WavUtility
{
    // Reads a 16-bit PCM WAV into float samples (interleaved if stereo). Returns null if it isn't one.
    public static float[] Decode(byte[] wav, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 1;
        if (wav == null || wav.Length <= 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;

        int offset = 0, length = 0;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "fmt " && pos + 16 <= wav.Length)
            {
                channels = Math.Max(1, (int)BitConverter.ToInt16(wav, pos + 10));
                sampleRate = BitConverter.ToInt32(wav, pos + 12);
            }
            else if (id == "data")
            {
                offset = pos + 8;
                // Streamed WAVs often leave the data size as 0 or -1, so fall back to "the rest of the file".
                int rest = wav.Length - offset;
                length = size > 0 && size <= rest ? size : rest;
                break;
            }
            pos += 8 + Math.Max(0, size) + (size & 1);
        }
        if (length < 2 || sampleRate <= 0) return null;

        var samples = new float[length / 2];
        for (int s = 0; s < samples.Length; s++)
            samples[s] = (short)(wav[offset + s * 2] | (wav[offset + s * 2 + 1] << 8)) / 32768f;
        return samples;
    }

    // 16-bit PCM WAV, in memory.
    public static byte[] Encode(float[] samples, int sampleRate, int channels)
    {
        int dataBytes = samples.Length * 2;
        using (var stream = new MemoryStream(44 + dataBytes))
        using (var w = new BinaryWriter(stream))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataBytes);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                               // fmt chunk size
            w.Write((short)1);                         // PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(sampleRate * channels * 2);        // byte rate
            w.Write((short)(channels * 2));            // block align
            w.Write((short)16);                        // bits per sample
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataBytes);
            foreach (float f in samples)
                w.Write((short)(Math.Max(-1f, Math.Min(1f, f)) * short.MaxValue));
            w.Flush();
            return stream.ToArray();
        }
    }
}
