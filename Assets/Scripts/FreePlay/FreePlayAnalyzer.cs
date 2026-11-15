using System;
using System.Collections.Generic;
using System.Linq;

// Free Play music analysis. Plain C#, no Unity calls, so it runs in EditMode tests and outside Unity.
// Feed NoteOn/NoteOff as they happen and call Poll every frame; a phrase ends when no key is held and
// phraseGapSeconds have passed since the last note event. Each finished phrase is analysed once: chords,
// key (Krumhansl-Kessler), steadiness, which hand, and whether the melody is the opening of a known song.
public class FreePlayAnalyzer
{
    public const float SteadyMaxCv = 0.15f;     // ioi CV below this = Steady
    public const float ABitUnevenMaxCv = 0.30f; // below this = ABitUneven, else Uneven
    public const int SteadinessMinOnsets = 5;
    public const double MaxNoteWeightSeconds = 2.0; // cap per note in the key histogram (held pedal notes)
    const double MinNoteWeightSeconds = 0.05;
    const double StaleHoldSeconds = 20.0; // a key "held" this long without any event is treated as a lost NoteOff

    static readonly string[] PcNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    // Krumhansl-Kessler key profiles, index 0 = tonic.
    static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    // Chord templates: intervals above the root (sorted), quality, name suffix. Order = preference when no
    // interpretation has its root in the bass (simplest first).
    struct ChordTemplate
    {
        public int[] intervals; public string quality; public string suffix;
        public ChordTemplate(string quality, string suffix, params int[] intervals) { this.quality = quality; this.suffix = suffix; this.intervals = intervals; }
    }
    static readonly ChordTemplate[] Templates =
    {
        new ChordTemplate("major", "", 0, 4, 7),
        new ChordTemplate("minor", "m", 0, 3, 7),
        new ChordTemplate("diminished", "dim", 0, 3, 6),
        new ChordTemplate("augmented", "aug", 0, 4, 8),
        new ChordTemplate("sus4", "sus4", 0, 5, 7),
        new ChordTemplate("sus2", "sus2", 0, 2, 7),
        new ChordTemplate("dominant 7th", "7", 0, 4, 7, 10),
        new ChordTemplate("major 7th", "maj7", 0, 4, 7, 11),
        new ChordTemplate("minor 7th", "m7", 0, 3, 7, 10),
        new ChordTemplate("half-diminished 7th", "m7b5", 0, 3, 6, 10),
        new ChordTemplate("diminished 7th", "dim7", 0, 3, 6, 9),
        new ChordTemplate("dominant 7th", "7", 0, 4, 10), // fifth left out, common in left-hand voicings
        new ChordTemplate("major 6th", "6", 0, 4, 7, 9),
        new ChordTemplate("minor 6th", "m6", 0, 3, 7, 9),
    };

    readonly List<SongTemplate> songs;
    readonly List<FreePlayNote> phrase = new List<FreePlayNote>();
    readonly Dictionary<int, FreePlayNote> held = new Dictionary<int, FreePlayNote>();
    readonly List<FreePlayNote> recent = new List<FreePlayNote>(); // session-wide, released notes, for the rolling key
    double lastEvent;
    PhraseAnalysis lastPhrase;

    public FreePlaySettings Settings { get; }
    public FreePlaySession Session { get; private set; } = new FreePlaySession();
    public bool PhraseInProgress => phrase.Count > 0;

    public FreePlayAnalyzer(FreePlaySettings settings, IEnumerable<SongTemplate> songs)
    {
        Settings = settings ?? new FreePlaySettings();
        this.songs = songs != null ? songs.Where(s => s != null && s.opening != null).ToList() : new List<SongTemplate>();
    }

    public void NoteOn(int midi, int velocity, double time)
    {
        if (velocity <= 0) { NoteOff(midi, time); return; } // MIDI running-status convention
        if (held.TryGetValue(midi, out var old)) old.release = time; // re-struck without a NoteOff
        var n = new FreePlayNote { midi = midi, velocity = velocity, onset = time };
        phrase.Add(n);
        held[midi] = n;
        lastEvent = time;
    }

    public void NoteOff(int midi, double time)
    {
        if (!held.TryGetValue(midi, out var n)) return;
        n.release = Math.Max(time, n.onset);
        held.Remove(midi);
        lastEvent = time;
    }

    // Call every frame. Returns the finished phrase exactly once, otherwise null.
    public PhraseAnalysis Poll(double now)
    {
        if (phrase.Count == 0) return null;
        if (held.Count > 0)
        {
            if (now - lastEvent < StaleHoldSeconds) return null;
            foreach (var h in held.Values) h.release = lastEvent;
            held.Clear();
        }
        else if (now - lastEvent < Settings.phraseGapSeconds) return null;

        foreach (var n in phrase) if (n.release < 0) n.release = lastEvent;
        recent.AddRange(phrase);
        int extra = recent.Count - Math.Max(1, Settings.keyWindowNotes);
        if (extra > 0) recent.RemoveRange(0, extra);
        var rolling = EstimateKey(recent, Settings);

        var a = Analyze(phrase, lastEvent, rolling);
        UpdateSession(a, rolling);
        phrase.Clear();
        lastPhrase = a;
        return a;
    }

    // The phrase so far (held notes released at `now`), else the last finished phrase, else null. Doesn't end the phrase.
    public PhraseAnalysis Peek(double now)
    {
        if (phrase.Count == 0) return lastPhrase;
        var copy = phrase.Select(n => new FreePlayNote { midi = n.midi, velocity = n.velocity, onset = n.onset, release = n.release < 0 ? Math.Max(now, n.onset) : n.release }).ToList();
        return Analyze(copy, Math.Max(now, lastEvent), Session.rollingKey);
    }

    public void Reset()
    {
        phrase.Clear();
        held.Clear();
        recent.Clear();
        lastPhrase = null;
        lastEvent = 0;
        Session = new FreePlaySession();
    }

    void UpdateSession(PhraseAnalysis a, KeyEstimate rolling)
    {
        var s = Session;
        s.phrases++;
        s.notes += a.noteCount;
        s.lowest = Math.Min(s.lowest, a.lowest);
        s.highest = Math.Max(s.highest, a.highest);
        if (a.song != null && !s.songsRecognized.Contains(a.song.songId)) s.songsRecognized.Add(a.song.songId);
        foreach (var c in a.chords) if (!s.chordsSeen.Contains(c.name)) s.chordsSeen.Add(c.name);
        s.rollingKey = rolling;
    }

    // notes: all released (release >= onset).
    PhraseAnalysis Analyze(List<FreePlayNote> notes, double end, KeyEstimate fallbackKey)
    {
        var sorted = notes.OrderBy(n => n.onset).ThenBy(n => n.midi).ToList();
        var groups = OnsetGroups(sorted, Settings.chordWindowSeconds);

        var a = new PhraseAnalysis
        {
            start = sorted[0].onset,
            end = end,
            noteCount = sorted.Count,
            lowest = sorted.Min(n => n.midi),
            highest = sorted.Max(n => n.midi),
            melody = groups.Select(g => g.Max(n => n.midi)).ToList(),
            chords = new List<ChordInfo>(),
        };

        foreach (var g in groups)
        {
            var c = IdentifyChord(g.Select(n => n.midi).ToList());
            if (c == null) continue;
            c.onset = g[0].onset;
            if (a.chords.Count > 0 && a.chords[a.chords.Count - 1].name == c.name) continue;
            a.chords.Add(c);
        }

        a.key = EstimateKey(sorted, Settings);
        var romanKey = a.key != null && a.key.IsClear ? a.key : fallbackKey != null && fallbackKey.IsClear ? fallbackKey : null;
        if (romanKey != null) a.romanNumerals = a.chords.Select(c => RomanNumeral(c, romanKey) ?? "?").ToList();

        a.steadiness = RateSteadiness(groups.Select(g => g[0].onset).ToList(), out a.ioiCv);
        a.song = MatchSong(a.melody, songs, Settings.songMatchMinNotes);
        a.hands = GuessHands(sorted, groups);
        return a;
    }

    static List<List<FreePlayNote>> OnsetGroups(List<FreePlayNote> sorted, double window)
    {
        var groups = new List<List<FreePlayNote>>();
        List<FreePlayNote> cur = null;
        foreach (var n in sorted)
        {
            if (cur == null || n.onset - cur[0].onset > window) { cur = new List<FreePlayNote>(); groups.Add(cur); }
            cur.Add(n);
        }
        return groups;
    }

    // Register heuristic: below middle C = left, middle C and up = right; a simultaneous spread wider than an
    // octave across middle C, or a real share of notes on both sides, = both.
    static string GuessHands(List<FreePlayNote> notes, List<List<FreePlayNote>> groups)
    {
        if (notes.Count < 3) return "unknown";
        foreach (var g in groups)
        {
            int lo = g.Min(n => n.midi), hi = g.Max(n => n.midi);
            if (hi - lo > 12 && lo < 60 && hi >= 60) return "both";
        }
        int low = notes.Count(n => n.midi < 60), high = notes.Count - low;
        if (Math.Min(low, high) >= 0.25 * notes.Count) return "both";
        return high >= low ? "right" : "left";
    }

    // ---- Pure helpers ----

    public static string PitchClassName(int pc) => PcNames[((pc % 12) + 12) % 12];

    public static ChordInfo IdentifyChord(IList<int> midis)
    {
        if (midis == null || midis.Count < 3) return null;
        var sorted = midis.OrderBy(m => m).ToArray();
        int bass = sorted[0], bassPc = Pc(bass);
        var pcs = sorted.Select(Pc).Distinct().ToList();
        if (pcs.Count < 3) return null;

        int bestT = -1, bestRoot = -1;
        bool bestInBass = false;
        foreach (int root in pcs)
        {
            var ivs = pcs.Select(p => (p - root + 12) % 12).OrderBy(x => x).ToArray();
            for (int t = 0; t < Templates.Length; t++)
            {
                if (!Templates[t].intervals.SequenceEqual(ivs)) continue;
                bool inBass = root == bassPc;
                // Root in the bass wins; otherwise the simplest (earliest) template.
                if (bestT < 0 || (inBass && !bestInBass) || (inBass == bestInBass && t < bestT))
                { bestT = t; bestRoot = root; bestInBass = inBass; }
            }
        }
        if (bestT < 0) return null;

        var tpl = Templates[bestT];
        int bassIv = (bassPc - bestRoot + 12) % 12;
        int inversion = bassIv == 0 ? 0 : bassIv <= 5 ? 1 : bassIv <= 8 ? 2 : 3; // 2nd/3rd/4th, 5th, 6th/7th
        string name = PcNames[bestRoot] + tpl.suffix + (inversion > 0 ? "/" + PcNames[bassPc] : "");
        return new ChordInfo
        {
            name = name, root = bestRoot, quality = tpl.quality, bassMidi = bass, inversion = inversion, midis = sorted,
        };
    }

    // Duration-weighted pitch-class histogram vs the 24 Krumhansl-Kessler profiles (Pearson r). Each note counts for
    // its length, capped at MaxNoteWeightSeconds; held notes (release < 0) last until the latest time in the list.
    // Null for no notes.
    public static KeyEstimate EstimateKey(IList<FreePlayNote> notes, FreePlaySettings s)
    {
        if (notes == null || notes.Count == 0) return null;
        s = s ?? new FreePlaySettings();
        double latest = notes.Max(n => Math.Max(n.onset, n.release));
        var hist = new double[12];
        foreach (var n in notes)
        {
            double end = n.release < 0 ? latest : n.release;
            double w = Math.Max(MinNoteWeightSeconds, Math.Min(MaxNoteWeightSeconds, end - n.onset));
            hist[Pc(n.midi)] += w;
        }

        double best = double.NegativeInfinity, second = double.NegativeInfinity;
        int bestTonic = 0; bool bestMajor = true;
        for (int mode = 0; mode < 2; mode++)
        {
            var prof = mode == 0 ? MajorProfile : MinorProfile;
            for (int tonic = 0; tonic < 12; tonic++)
            {
                var p = new double[12];
                for (int pc = 0; pc < 12; pc++) p[pc] = prof[(pc - tonic + 12) % 12];
                double r = Pearson(hist, p);
                if (r > best) { second = best; best = r; bestTonic = tonic; bestMajor = mode == 0; }
                else if (r > second) second = r;
            }
        }
        if (double.IsNaN(best) || double.IsInfinity(best)) best = 0;
        if (double.IsNaN(second) || double.IsInfinity(second)) second = best;

        int distinct = hist.Count(h => h > 0);
        var k = new KeyEstimate
        {
            tonic = bestTonic, major = bestMajor,
            name = PcNames[bestTonic] + (bestMajor ? " major" : " minor"),
            correlation = (float)best, margin = (float)(best - second),
        };
        k.IsClear = distinct >= 3 && k.correlation >= s.keyMinCorrelation && k.margin >= s.keyMinMargin;
        return k;
    }

    static double Pearson(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    // Coefficient of variation of the inter-onset intervals (one onset per onset group).
    public static Steadiness RateSteadiness(IList<double> onsets, out float cv)
    {
        cv = 0f;
        if (onsets == null || onsets.Count < SteadinessMinOnsets) return Steadiness.NotEnoughNotes;
        var o = onsets.OrderBy(x => x).ToList();
        var ioi = new List<double>();
        for (int i = 1; i < o.Count; i++) ioi.Add(o[i] - o[i - 1]);
        double mean = ioi.Average();
        if (mean <= 0) return Steadiness.NotEnoughNotes;
        double sd = Math.Sqrt(ioi.Sum(d => (d - mean) * (d - mean)) / ioi.Count);
        cv = (float)(sd / mean);
        return cv < SteadyMaxCv ? Steadiness.Steady : cv < ABitUnevenMaxCv ? Steadiness.ABitUneven : Steadiness.Uneven;
    }

    // Transposition-invariant: a run of >= minNotes consecutive melody notes whose intervals equal the song opening's,
    // starting at the song's first note (the run itself may start anywhere in the melody). The matched span must be
    // distinctive (see IsDistinctive). Most matched notes wins; ties go to the first song in the list.
    public static SongMatch MatchSong(IList<int> melody, IEnumerable<SongTemplate> songs, int minNotes)
    {
        if (melody == null || songs == null || melody.Count < 2) return null;
        minNotes = Math.Max(2, minNotes);
        var mi = Intervals(melody);
        SongMatch best = null;
        foreach (var song in songs)
        {
            if (song?.opening == null || song.opening.Length < minNotes) continue;
            var ti = Intervals(song.opening);
            for (int s = 0; s < mi.Count; s++)
            {
                int k = 0;
                while (s + k < mi.Count && k < ti.Count && mi[s + k] == ti[k]) k++;
                int matched = k + 1;
                if (matched < minNotes || (best != null && matched <= best.matchedNotes)) continue;
                if (!IsDistinctive(ti.GetRange(0, k))) continue;
                best = new SongMatch { songId = song.id, songName = song.name, matchedNotes = matched, transpose = melody[s] - song.opening[0] };
            }
        }
        return best;
    }

    // A melodic span (as intervals) that can't be a scale or noodling: has a skip (|interval| >= 3), or has a
    // repeated note AND a change of direction AND >= 8 notes.
    public static bool IsDistinctive(IList<int> intervals)
    {
        if (intervals == null || intervals.Count == 0) return false;
        if (intervals.Any(d => Math.Abs(d) >= 3)) return true;
        bool repeat = intervals.Any(d => d == 0);
        int lastSign = 0; bool turn = false;
        foreach (int d in intervals)
        {
            int sign = Math.Sign(d);
            if (sign == 0) continue;
            if (lastSign != 0 && sign != lastSign) turn = true;
            lastSign = sign;
        }
        return repeat && turn && intervals.Count + 1 >= 8;
    }

    static List<int> Intervals(IList<int> notes)
    {
        var r = new List<int>(Math.Max(0, notes.Count - 1));
        for (int i = 1; i < notes.Count; i++) r.Add(notes[i] - notes[i - 1]);
        return r;
    }

    // Roman numeral of a chord in a key: uppercase major/augmented/dominant, lowercase minor/diminished; "°" diminished,
    // "+" augmented, "7"/"maj7"/"ø7"/"°7" sevenths, "6", "sus2"/"sus4". Major keys also accept bIII, bVI, bVII.
    // Null when the root isn't diatonic-ish (or no chord/key).
    public static string RomanNumeral(ChordInfo chord, KeyEstimate key)
    {
        if (chord == null || key == null) return null;
        int d = (chord.root - key.tonic + 12) % 12;
        string numeral;
        if (key.major)
        {
            switch (d)
            {
                case 0: numeral = "I"; break;
                case 2: numeral = "II"; break;
                case 3: numeral = "bIII"; break;
                case 4: numeral = "III"; break;
                case 5: numeral = "IV"; break;
                case 7: numeral = "V"; break;
                case 8: numeral = "bVI"; break;
                case 9: numeral = "VI"; break;
                case 10: numeral = "bVII"; break;
                case 11: numeral = "VII"; break;
                default: return null;
            }
        }
        else
        {
            switch (d)
            {
                case 0: numeral = "I"; break;
                case 2: numeral = "II"; break;
                case 3: numeral = "III"; break;
                case 5: numeral = "IV"; break;
                case 7: numeral = "V"; break;
                case 8: numeral = "VI"; break;
                case 10: numeral = "VII"; break;
                case 11: numeral = "VII"; break; // leading-tone chord (harmonic minor)
                default: return null;
            }
        }

        string suffix;
        bool lower;
        switch (chord.quality)
        {
            case "minor": lower = true; suffix = ""; break;
            case "diminished": lower = true; suffix = "°"; break;
            case "augmented": lower = false; suffix = "+"; break;
            case "sus2": lower = false; suffix = "sus2"; break;
            case "sus4": lower = false; suffix = "sus4"; break;
            case "dominant 7th": lower = false; suffix = "7"; break;
            case "major 7th": lower = false; suffix = "maj7"; break;
            case "minor 7th": lower = true; suffix = "7"; break;
            case "half-diminished 7th": lower = true; suffix = "ø7"; break;
            case "diminished 7th": lower = true; suffix = "°7"; break;
            case "major 6th": lower = false; suffix = "6"; break;
            case "minor 6th": lower = true; suffix = "6"; break;
            default: lower = false; suffix = ""; break; // major
        }
        if (lower)
        {
            // keep a leading "b" lowercase-safe: "bVII" -> "bvii"
            numeral = numeral.ToLowerInvariant();
        }
        return numeral + suffix;
    }

    static int Pc(int midi) => ((midi % 12) + 12) % 12;
}
