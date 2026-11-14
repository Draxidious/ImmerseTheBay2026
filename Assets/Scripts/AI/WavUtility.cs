using System;
using System.IO;
using System.Text;

public static class WavUtility
{
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
