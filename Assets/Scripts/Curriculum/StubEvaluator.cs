using System;
using System.Collections.Generic;

// In-order note matching, so the curriculum works end to end before the real evaluator lands.
// Presses are aligned to the expected notes in sequence (like a diff): the alignment keeps as many notes as possible,
// and among equally good ones prefers the one whose gaps between presses best follow the written gaps at the student's
// own tempo. Absolute due times only anchor the first note, so steady slow or fast playing is all hits, and a freeze
// followed by the right notes played late is hits plus one hesitation.
// A matched note is right on the first try unless a wrong key was pressed while it was the next note. Misses and
// wrong keys cost accuracy; tempo is reported separately (tempoRatio) and timing credit is informational.
public class StubEvaluator : IPerformanceEvaluator
{
    const double MatchValue = 10, MaxTimingPenalty = 3; // one more matched note always beats any timing preference
    const double IntervalSlack = 0.25;  // the window grows with the gap being predicted: tempo errors add up over it
    const double ChordSeconds = 0.05;   // presses this close together are one chord, in any order
    const double BounceSeconds = 0.08;  // the same key again this fast is a key bounce, not a new press
    const int Lookback = 16;            // notes back an alignment step considers in detail; beyond that a capped jump
    const float MinRatio = 0.25f, MaxRatio = 4f;

    readonly LessonConfig cfg;

    public StubEvaluator(LessonConfig cfg)
    {
        this.cfg = cfg;
    }

    public EvaluationResult Evaluate(List<PlayedNote> played, List<ExpectedNote> expected, int startBar, int endBar)
    {
        try
        {
            return Judge(played, expected, startBar, endBar);
        }
        catch (Exception ex)
        {
            // Same policy as the engine: a broken evaluation must not fail the student or stall the lesson.
            UnityEngine.Debug.LogException(ex);
            var result = new EvaluationResult { overallScore = 1f, timingScore = 1f, passed = true };
            for (int bar = startBar; bar <= endBar; bar++)
                result.bars.Add(new BarResult { barNumber = bar, score = 1f, timingScore = 1f, passed = true });
            return result;
        }
    }

    EvaluationResult Judge(List<PlayedNote> played, List<ExpectedNote> expected, int startBar, int endBar)
    {
        double window = Math.Max(0.05, cfg.matchWindowMs / 1000.0);
        var exp = Notes(expected, startBar, endBar);
        // The first note has no earlier press to measure from, only the clock: allow about a beat either side.
        double firstWindow = Math.Max(window, Math.Min(2.0, BeatSeconds(exp)));
        var prs = Presses(played, -firstWindow);
        int n = exp.Count, m = prs.Count;

        // Align at the asked tempo, measure the student's tempo, then align again following it.
        int[] match = Align(exp, prs, 1.0, window, firstWindow);
        float ratio = TempoAfterPauses(exp, prs, match);
        match = Align(exp, prs, ratio, window, firstWindow);
        ratio = TempoAfterPauses(exp, prs, match);
        var excess = Excess(exp, prs, match, ratio);
        var paused = Paused(excess);

        // Unmatched presses are wrong keys, charged to the note that was next when they were pressed.
        var noteOfPress = new int[m];
        for (int j = 0; j < m; j++) noteOfPress[j] = -1;
        for (int i = 0; i < n; i++) if (match[i] >= 0) noteOfPress[match[i]] = i;
        var wrongs = new List<int>[n];
        int lastNote = -1;
        for (int j = 0; j < m; j++)
        {
            if (noteOfPress[j] >= 0) { lastNote = Math.Max(lastNote, noteOfPress[j]); continue; }
            int next = lastNote + 1;
            if (next >= n) continue; // playing on after the last note
            if (j > 0 && prs[j - 1].midi == prs[j].midi && prs[j].time - prs[j - 1].time < BounceSeconds) continue;
            if (prs[j].midi == exp[next].midi) continue; // the right key struck early or twice; another press of it counted
            (wrongs[next] = wrongs[next] ?? new List<int>()).Add(prs[j].midi);
        }

        var result = new EvaluationResult();
        var bars = new Dictionary<int, BarResult>();
        for (int bar = startBar; bar <= endBar; bar++)
        {
            var b = new BarResult { barNumber = bar };
            result.bars.Add(b);
            bars[bar] = b;
        }

        var notes = new Dictionary<int, int>();
        var hits = new Dictionary<int, int>();
        var credit = new Dictionary<int, float>();
        int totalNotes = 0, totalHits = 0, totalFirstTry = 0;
        float totalCredit = 0f;
        for (int i = 0; i < n; i++)
        {
            var e = exp[i];
            var b = bars[e.bar];
            notes[e.bar] = Get(notes, e.bar) + 1;
            totalNotes++;
            if (wrongs[i] != null)
                foreach (int midi in wrongs[i]) b.wrongNotes.Add(new WrongNote { bar = e.bar, expected = e.midi, played = midi });
            if (match[i] < 0 || wrongs[i] != null) b.firstTryMisses++;
            else totalFirstTry++;
            if (match[i] < 0) continue;

            hits[e.bar] = Get(hits, e.bar) + 1;
            totalHits++;
            // excess is the signed distance from where the previous press and the student's tempo put this note.
            float c = cfg.TimingCredit((float)(excess[i] * 1000.0));
            credit[e.bar] = Get(credit, e.bar) + c;
            totalCredit += c;
            if (paused[i]) b.hesitations.Add(new Hesitation { bar = e.bar, note = e.midi, ms = (int)(excess[i] * 1000.0) });
        }

        var barRatios = new Dictionary<int, List<float>>();
        foreach (var (i, r) in Pairs(exp, prs, match, paused))
        {
            if (!barRatios.TryGetValue(exp[i + 1].bar, out var list)) barRatios[exp[i + 1].bar] = list = new List<float>();
            list.Add(r);
        }

        foreach (var b in result.bars)
        {
            int nb = Get(notes, b.barNumber), hb = Get(hits, b.barNumber);
            b.score = nb == 0 ? 1f : (float)(nb - b.firstTryMisses) / nb;
            b.timingScore = hb == 0 ? (nb == 0 ? 1f : 0f) : Get(credit, b.barNumber) / hb;
            b.tempoRatio = barRatios.TryGetValue(b.barNumber, out var list) ? Median(list) : 1f;
            b.passed = b.score >= cfg.passAccuracy;
        }
        result.overallScore = totalNotes == 0 ? 1f : (float)totalFirstTry / totalNotes;
        result.timingScore = totalHits == 0 ? (totalNotes == 0 ? 1f : 0f) : totalCredit / totalHits;
        result.tempoRatio = ratio;
        result.passed = result.overallScore >= cfg.passAccuracy;
        return result;
    }

    // The run's notes in playing order. Notes struck together (a chord) are put in pitch order, as their presses are.
    static List<ExpectedNote> Notes(List<ExpectedNote> expected, int startBar, int endBar)
    {
        var exp = new List<ExpectedNote>();
        if (expected != null)
            foreach (var e in expected)
                if (e != null && e.bar >= startBar && e.bar <= endBar) exp.Add(e);
        SortGroups(exp, (a, b) => Math.Abs(b.time - a.time) < 0.001, (a, b) => a.midi.CompareTo(b.midi));
        return exp;
    }

    // Presses in time order, chords in pitch order. Presses long before the first note (during the count-in) are dropped.
    static List<PlayedNote> Presses(List<PlayedNote> played, double earliest)
    {
        var prs = new List<PlayedNote>();
        if (played != null)
            foreach (var p in played)
                if (p != null && p.time >= earliest) prs.Add(p);
        var order = new Dictionary<PlayedNote, int>();
        for (int j = 0; j < prs.Count; j++) order[prs[j]] = j;
        prs.Sort((a, b) => a.time != b.time ? a.time.CompareTo(b.time) : order[a].CompareTo(order[b]));
        SortGroups(prs, (a, b) => b.time - a.time < ChordSeconds, (a, b) => a.midi != b.midi ? a.midi.CompareTo(b.midi) : a.time.CompareTo(b.time));
        return prs;
    }

    // Sorts each run of neighbours that belong together (together(previous, next)) with order, leaving the rest alone.
    static void SortGroups<T>(List<T> list, Func<T, T, bool> together, Comparison<T> order)
    {
        int start = 0;
        for (int k = 1; k <= list.Count; k++)
        {
            if (k < list.Count && together(list[k - 1], list[k])) continue;
            if (k - start > 1)
            {
                var group = list.GetRange(start, k - start);
                group.Sort(order);
                for (int g = 0; g < group.Count; g++) list[start + g] = group[g];
            }
            start = k;
        }
    }

    static double BeatSeconds(List<ExpectedNote> exp)
    {
        for (int i = 1; i < exp.Count; i++)
        {
            double beats = exp[i].beat - exp[i - 1].beat, secs = exp[i].time - exp[i - 1].time;
            if (beats > 0.001 && secs > 0.001) return secs / beats;
        }
        return 0;
    }

    // The best in-order alignment: match[i] = press index of note i, or -1 when the note was missed.
    // best[i][j] scores the best alignment whose last match is note i to press j: MatchValue per matched note, minus a
    // capped penalty for how far each press lands from where the previous one and the tempo put it. A press may be
    // late by any amount (a freeze), but early by no more than the window (that would be skipping ahead).
    static int[] Align(List<ExpectedNote> exp, List<PlayedNote> prs, double ratio, double window, double firstWindow)
    {
        int n = exp.Count, m = prs.Count;
        var match = new int[n];
        for (int i = 0; i < n; i++) match[i] = -1;
        if (n == 0 || m == 0) return match;

        var cand = new List<int>[n]; // presses of the right key for each note, in order
        for (int i = 0; i < n; i++)
        {
            cand[i] = new List<int>();
            for (int j = 0; j < m; j++) if (prs[j].midi == exp[i].midi) cand[i].Add(j);
        }

        const double None = double.NegativeInfinity;
        var best = new double[n][];
        var from = new int[n][];  // previous match, encoded as i * m + j; -1 = this is the first match
        var top = new double[n][];   // top[i][j]: best over notes <= i and presses <= j, for the capped long jump
        var topAt = new int[n][];
        for (int i = 0; i < n; i++)
        {
            best[i] = new double[m];
            from[i] = new int[m];
            for (int j = 0; j < m; j++) best[i][j] = None;

            foreach (int j in cand[i])
            {
                double pred = Math.Max(0, exp[i].time / ratio);
                double s = None;
                int f = -1;
                double dev = prs[j].time - pred, tol = firstWindow + IntervalSlack * pred;
                if (dev >= -tol) s = MatchValue - Penalty(dev, tol);

                for (int i2 = i - 1; i2 >= 0 && i2 >= i - Lookback; i2--)
                {
                    double gap = Math.Max(0, (exp[i].time - exp[i2].time) / ratio);
                    double t = window + IntervalSlack * gap;
                    foreach (int j2 in cand[i2])
                    {
                        if (j2 >= j) break;
                        if (best[i2][j2] == None) continue;
                        double d = (prs[j].time - prs[j2].time) - gap;
                        if (d < -t) continue;
                        double v = best[i2][j2] + MatchValue - Penalty(d, t);
                        if (v > s) { s = v; f = i2 * m + j2; }
                    }
                }

                int far = i - Lookback - 1;
                if (far >= 0 && j > 0 && top[far][j - 1] != None)
                {
                    double v = top[far][j - 1] + MatchValue - MaxTimingPenalty;
                    if (v > s) { s = v; f = topAt[far][j - 1]; }
                }
                best[i][j] = s;
                from[i][j] = f;
            }

            top[i] = new double[m];
            topAt[i] = new int[m];
            for (int j = 0; j < m; j++)
            {
                double v = best[i][j];
                int at = i * m + j;
                if (j > 0 && top[i][j - 1] > v) { v = top[i][j - 1]; at = topAt[i][j - 1]; }
                if (i > 0 && top[i - 1][j] > v) { v = top[i - 1][j]; at = topAt[i - 1][j]; }
                top[i][j] = v;
                topAt[i][j] = at;
            }
        }

        if (top[n - 1][m - 1] == None) return match;
        int cell = topAt[n - 1][m - 1];
        while (cell >= 0)
        {
            int i = cell / m, j = cell % m;
            match[i] = j;
            cell = from[i][j];
        }
        return match;
    }

    static double Penalty(double dev, double tol) => Math.Min(MaxTimingPenalty, Math.Abs(dev) / Math.Max(0.001, tol));

    // Per matched note: how much later than expected it came, in seconds, measured from the previous matched press and
    // the notated gap at the student's tempo (from the clock for the first one). Negative = early. 0 for missed notes.
    static double[] Excess(List<ExpectedNote> exp, List<PlayedNote> prs, int[] match, float ratio)
    {
        var excess = new double[exp.Count];
        int prev = -1;
        for (int i = 0; i < exp.Count; i++)
        {
            if (match[i] < 0) continue;
            double t = prs[match[i]].time;
            excess[i] = prev < 0
                ? t - exp[i].time / ratio
                : (t - prs[match[prev]].time) - (exp[i].time - exp[prev].time) / ratio;
            prev = i;
        }
        return excess;
    }

    bool[] Paused(double[] excess)
    {
        var paused = new bool[excess.Length];
        for (int i = 0; i < excess.Length; i++) paused[i] = excess[i] * 1000.0 >= cfg.timingZeroCreditMs;
        return paused;
    }

    // The student's tempo: median pair ratio, measured again without the pairs that span a pause.
    float TempoAfterPauses(List<ExpectedNote> exp, List<PlayedNote> prs, int[] match)
    {
        float rough = Overall(Pairs(exp, prs, match, null), 1f);
        return Overall(Pairs(exp, prs, match, Paused(Excess(exp, prs, match, rough))), rough);
    }

    // Under 2 pairs is too little to tell a tempo from a stumble.
    static float Overall(List<(int, float)> pairs, float fallback)
    {
        if (pairs.Count < 2) return fallback;
        var list = new List<float>();
        foreach (var (_, r) in pairs) list.Add(r);
        return Median(list);
    }

    // Consecutive notes both matched, with a written gap: (index of the first, written gap / played gap).
    static List<(int, float)> Pairs(List<ExpectedNote> exp, List<PlayedNote> prs, int[] match, bool[] paused)
    {
        var pairs = new List<(int, float)>();
        for (int i = 0; i + 1 < exp.Count; i++)
        {
            if (match[i] < 0 || match[i + 1] < 0) continue;
            if (paused != null && paused[i + 1]) continue;
            double written = exp[i + 1].time - exp[i].time, playedGap = prs[match[i + 1]].time - prs[match[i]].time;
            if (written <= 0.001 || playedGap <= 0.001) continue;
            pairs.Add((i, (float)Math.Max(MinRatio, Math.Min(MaxRatio, written / playedGap))));
        }
        return pairs;
    }

    static float Median(List<float> values)
    {
        if (values.Count == 0) return 1f;
        values.Sort();
        int k = values.Count / 2;
        return values.Count % 2 == 1 ? values[k] : (values[k - 1] + values[k]) / 2f;
    }

    static int Get(Dictionary<int, int> d, int key) => d.TryGetValue(key, out var v) ? v : 0;
    static float Get(Dictionary<int, float> d, int key) => d.TryGetValue(key, out var v) ? v : 0f;
}
