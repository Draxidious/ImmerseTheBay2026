using System;
using System.Collections.Generic;

// Simple timed note matching so the curriculum works end to end before the real evaluator lands.
// Each expected note, in order, takes the first unused press of the same key within matchWindowMs of its time.
// Matched = hit. Bar accuracy = hits / notes in the bar. Timing credit per hit is 1 within timingFullCreditMs of the
// note's time, fading to 0 at timingZeroCreditMs; bar timing = mean credit over its hits. Presses that matched
// nothing are wrong notes, charged to the expected note closest in time.
// Hesitations: a silence (no key at all) that lets notes go by and lasts longer than the music's own gaps around it.
// Measuring against the notated gaps means rests and long notes never count as hesitating.
public class StubEvaluator : IPerformanceEvaluator
{
    readonly LessonConfig cfg;

    public StubEvaluator(LessonConfig cfg)
    {
        this.cfg = cfg;
    }

    public EvaluationResult Evaluate(List<PlayedNote> played, List<ExpectedNote> expected, int startBar, int endBar)
    {
        played = played ?? new List<PlayedNote>();
        expected = expected ?? new List<ExpectedNote>();
        double window = cfg.matchWindowMs / 1000.0;

        var result = new EvaluationResult();
        var bars = new Dictionary<int, BarResult>();
        var notes = new Dictionary<int, int>();
        var hits = new Dictionary<int, int>();
        var credit = new Dictionary<int, float>();
        for (int bar = startBar; bar <= endBar; bar++)
        {
            var b = new BarResult { barNumber = bar };
            result.bars.Add(b);
            bars[bar] = b;
            notes[bar] = hits[bar] = 0;
            credit[bar] = 0f;
        }

        var used = new bool[played.Count];
        int cursor = 0; // matches move forward through the presses, so one press cannot serve two notes out of order
        foreach (var e in expected)
        {
            if (!notes.ContainsKey(e.bar)) continue;
            notes[e.bar]++;
            for (int i = cursor; i < played.Count; i++)
            {
                if (used[i] || played[i].midi != e.midi) continue;
                double dt = played[i].time - e.time;
                if (dt < -window) continue;
                if (dt > window) break;
                used[i] = true;
                cursor = i + 1;
                hits[e.bar]++;
                credit[e.bar] += cfg.TimingCredit((float)(dt * 1000.0));
                break;
            }
        }

        for (int i = 0; i < played.Count; i++)
        {
            if (used[i] || expected.Count == 0) continue;
            var near = Nearest(expected, played[i].time);
            if (bars.TryGetValue(near.bar, out var b))
                b.wrongNotes.Add(new WrongNote { bar = near.bar, expected = near.midi, played = played[i].midi });
        }

        AddHesitations(played, expected, bars, window);

        int totalHits = 0, totalNotes = 0;
        float totalCredit = 0f;
        foreach (var b in result.bars)
        {
            int n = notes[b.barNumber], h = hits[b.barNumber];
            b.score = n == 0 ? 1f : (float)h / n;
            b.timingScore = h == 0 ? (n == 0 ? 1f : 0f) : credit[b.barNumber] / h;
            b.passed = b.score >= cfg.accuracyThreshold;
            totalHits += h;
            totalNotes += n;
            totalCredit += credit[b.barNumber];
        }
        result.overallScore = totalNotes == 0 ? 1f : (float)totalHits / totalNotes;
        result.timingScore = totalHits == 0 ? (totalNotes == 0 ? 1f : 0f) : totalCredit / totalHits;
        result.passed = result.overallScore >= cfg.accuracyThreshold;
        return result;
    }

    // Silences between consecutive key presses (and from the first note's time / until the last note's time).
    // A silence counts when notes were due inside it and it is longer than the longest notated gap around those notes.
    void AddHesitations(List<PlayedNote> played, List<ExpectedNote> expected, Dictionary<int, BarResult> bars, double window)
    {
        if (expected.Count == 0) return;
        var times = new List<double> { -0.001 };
        foreach (var p in played) times.Add(p.time);
        times.Add(expected[expected.Count - 1].time + window + 0.001);
        times.Sort();

        for (int k = 1; k < times.Count; k++)
        {
            double a = times[k - 1], b = times[k];
            int first = -1, last = -1;
            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i].time <= a || expected[i].time >= b - window) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) continue; // nothing was skipped over

            double gap = 0;
            for (int i = Math.Max(1, first); i <= Math.Min(expected.Count - 1, last + 1); i++)
                gap = Math.Max(gap, expected[i].time - expected[i - 1].time);
            int excessMs = (int)((b - a - gap) * 1000.0);
            if (excessMs < cfg.timingZeroCreditMs) continue;

            var e = expected[first];
            if (bars.TryGetValue(e.bar, out var bar)) bar.hesitations.Add(new Hesitation { bar = e.bar, note = e.midi, ms = excessMs });
        }
    }

    static ExpectedNote Nearest(List<ExpectedNote> expected, double time)
    {
        var best = expected[0];
        foreach (var e in expected)
            if (Math.Abs(e.time - time) < Math.Abs(best.time - time)) best = e;
        return best;
    }
}
