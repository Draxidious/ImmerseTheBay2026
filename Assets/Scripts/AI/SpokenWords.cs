using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// The words a non-visual readout is built from ("E", "D sharp", "finger five", "Right hand"), each spoken once by Groq TTS
// and kept as a clip, so a readout plays at once instead of waiting for (and slowly speaking) a whole TTS sentence.
// Preloaded when non-visual mode starts. Without Groq the readout falls back to a TTS sentence.
public static class SpokenWords
{
    const float KeepAfter = 0.02f; // VoiceOutput puts its own gap between words

    static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    static Task loading;

    public static readonly string[] HandWords = { "Right hand", "Left hand" };

    public static IEnumerable<string> All()
    {
        for (int pc = 0; pc < 12; pc++) yield return CoachFacts.SpokenNoteName(60 + pc);
        for (int f = 1; f <= 5; f++) yield return NonVisualGuide.FingerWords(f);
        foreach (var h in HandWords) yield return h;
    }

    public static Task Preload() => loading ??= Load();

    static async Task Load()
    {
        var tts = new GroqTts(AIConfig.Get(), await AISecrets.Load());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var words = new List<string>(All());
        // A few at a time, to stay clear of the TTS rate limit.
        for (int i = 0; i < words.Count; i += 4)
        {
            var batch = words.GetRange(i, Mathf.Min(4, words.Count - i));
            // "D." reads as a word; on its own the model sometimes pads a letter with seconds of sound, so cap it too.
            var results = await Task.WhenAll(batch.ConvertAll(w => tts.Synthesize(w + ".", KeepAfter)));
            for (int j = 0; j < batch.Count; j++)
                if (results[j] != null) clips[batch[j]] = Cap(results[j], MaxSeconds);
        }
        Debug.Log($"[NonVisual] Cached {clips.Count}/{words.Count} spoken words in {sw.ElapsedMilliseconds} ms.");
        if (clips.Count < words.Count) loading = null; // try again next time
    }

    const float MaxSeconds = 0.8f, FadeSeconds = 0.03f;

    // The first maxSeconds of the clip, faded out at the cut so it doesn't click.
    static AudioClip Cap(AudioClip clip, float maxSeconds)
    {
        int frames = Mathf.Min(clip.samples, Mathf.RoundToInt(maxSeconds * clip.frequency));
        if (frames >= clip.samples) return clip;
        var data = new float[frames * clip.channels];
        clip.GetData(data, 0);
        int fade = Mathf.RoundToInt(FadeSeconds * clip.frequency);
        for (int f = 0; f < fade; f++)
            for (int c = 0; c < clip.channels; c++)
                data[(frames - 1 - f) * clip.channels + c] *= (float)f / fade;
        var capped = AudioClip.Create(clip.name, frames, clip.channels, clip.frequency, false);
        capped.SetData(data, 0);
        return capped;
    }

    // All the words as clips, or null if any is missing (then the caller speaks the text instead).
    public static List<AudioClip> Get(IEnumerable<string> words)
    {
        var list = new List<AudioClip>();
        foreach (var w in words)
        {
            if (!clips.TryGetValue(w, out var clip) || clip == null) return null;
            list.Add(clip);
        }
        return list;
    }
}
