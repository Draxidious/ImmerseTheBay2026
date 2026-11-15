using UnityEngine;

// Short sounds for a passed lesson run, so a pass is heard without words. Low, plucked tones (a decaying note with a soft
// octave) so they never sound like the "hey teacher" chime, which is two high, flat sine tones (880 then 1320 Hz).
public static class PassCues
{
    public enum Kind { None, Pass, StepUp, Milestone }

    // Pass: the next run is the same bars and hand, faster (or the same again). StepUp: a new hand or new bars.
    // Milestone: a measure learned, a play-through passed or the song finished. Nothing for failed runs or outside the lesson.
    public static Kind For(CoachContext ctx, string cue)
    {
        var c = ctx?.curriculum;
        if (c == null || ctx.mode != "curriculum" || ctx.trigger == CoachTrigger.UserAsked) return Kind.None;
        if (ctx.trigger == CoachTrigger.SongComplete) return Kind.Milestone;
        if (!c.passed || (ctx.thisRun != null && ctx.thisRun.nothingPlayed)) return Kind.None;
        if (ctx.trigger == CoachTrigger.MeasureLearned || ctx.trigger == CoachTrigger.PlayThroughPassed) return Kind.Milestone;
        return string.IsNullOrEmpty(cue) ? Kind.Pass : Kind.StepUp;
    }

    const int Rate = 44100;
    const float C5 = 523.25f, E5 = 659.25f, G5 = 783.99f, G4 = 392f;

    public static AudioClip Make(Kind kind)
    {
        switch (kind)
        {
            case Kind.Pass: return Notes("PassCue", new[] { C5 }, 0.15f, 0.15f);
            case Kind.StepUp: return Notes("StepUpCue", new[] { G4, C5 }, 0.15f, 0.15f);
            case Kind.Milestone: return Notes("MilestoneCue", new[] { C5, E5, G5 }, 0.12f, 0.3f);
            default: return null;
        }
    }

    // Notes one after another, each starting step seconds after the last; the last one rings for ring seconds.
    // Each note: quick fade in, exponential decay, a quiet octave above for a bell-like tone, faded to 0 at its end.
    static AudioClip Notes(string name, float[] freqs, float step, float ring)
    {
        float total = step * (freqs.Length - 1) + ring;
        var data = new float[Mathf.CeilToInt(total * Rate)];
        for (int n = 0; n < freqs.Length; n++)
        {
            int start = Mathf.RoundToInt(n * step * Rate);
            float length = n == freqs.Length - 1 ? ring : step * 1.6f; // earlier notes overlap the next a little
            int count = Mathf.Min(Mathf.RoundToInt(length * Rate), data.Length - start);
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)Rate;
                float env = Mathf.Min(1f, t / 0.005f) * Mathf.Exp(-t * 18f) * Mathf.Clamp01((length - t) / 0.02f);
                float w = 2f * Mathf.PI * freqs[n] * t;
                data[start + i] += (Mathf.Sin(w) + 0.25f * Mathf.Sin(2f * w)) * env * 0.45f;
            }
        }
        var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
