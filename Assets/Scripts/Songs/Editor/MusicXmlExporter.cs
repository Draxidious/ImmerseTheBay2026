using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;

/// <summary>
/// Writes a song from the song database as MusicXML (partwise, one piano part, two staves: treble = RH, bass = LH),
/// for engraving with Verovio (Tools/SheetMusic/render_sheets.py). Each written note gets an id so the rendered sheet
/// can be mapped back to the database note: "n{index}" for the note's first (or only) piece, "n{index}c{k}" for tied
/// continuation pieces (index = position in SongData.notes).
///
/// Handles: pickup bar (implicit measure 0), notes split at bar lines and into tied printable lengths, chords, extra voices
/// when notes in one hand overlap, rests, displayed accidentals (key-aware, reset each bar), beams per beat group,
/// fingering, key/time signature, tempo, title/composer.
/// </summary>
public static class MusicXmlExporter
{
    const int Div = 48; // divisions per quarter note: exact for 64ths, 32nds and triplets

    /// <summary>Print finger numbers (from the song database) above the notes. Set from Tools ▸ Songs ▸ Sheet Music: Show Fingering.</summary>
    public static bool IncludeFingering = true;

    static readonly string[] SharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    static readonly string[] FlatNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };
    static readonly string SharpOrder = "FCGDAEB";
    static readonly string FlatOrder = "BEADGCF";

    // Printable lengths in quarters, longest first (with dots).
    static readonly (double q, string type, int dots)[] Lengths =
    {
        (4, "whole", 0), (3, "half", 1), (2, "half", 0), (1.5, "quarter", 1), (1, "quarter", 0),
        (0.75, "eighth", 1), (0.5, "eighth", 0), (0.375, "16th", 1), (0.25, "16th", 0),
        (0.1875, "32nd", 1), (0.125, "32nd", 0), (0.0625, "64th", 0),
    };

    public class Result
    {
        public string xml;
        public SongData song;            // the notes the ids refer to
        public bool hasPickup;
        public int barCount;
    }

    class Seg
    {
        public int start, dur, midi, finger, noteIndex, piece;
        public bool tieStart, tieStop;
        public string Id => piece == 0 ? $"n{noteIndex}" : $"n{noteIndex}c{piece}";
    }

    class Ev   // one printed note/chord/rest of one length
    {
        public int start, dur; public string type; public int dots;
        public List<Seg> notes = new List<Seg>(); // empty = rest
        public bool tieStart, tieStop;   // for split pieces of the same chord
        public bool firstPiece;          // fingering only on the first printed piece
        public string[] beams = new string[4];
        public bool IsRest => notes.Count == 0;
    }

    public static Result Export(SongEntry e)
    {
        var song = SongNotation.ToSongData(e);
        int den = BeatUnitDenominator(e.beatUnit);
        double quartersPerBeat = 4.0 / den;
        int beatDiv = (int)Math.Round(Div * quartersPerBeat);
        int barDiv = beatDiv * e.beatsPerBar;

        float minBeat = song.notes.Where(n => n.bar == 1).Select(n => n.beat).DefaultIfEmpty(1f).Min();
        double pickupBeats = minBeat < 1f ? 1.0 - minBeat : 0.0;
        int pickupDiv = (int)Math.Round(pickupBeats * beatDiv);
        int bars = song.BarCount;

        int Abs(SongNote n) => (int)Math.Round((pickupBeats + (n.bar - 1) * e.beatsPerBar + (n.beat - 1)) * beatDiv);
        int MeasureStart(int m) => m == 0 ? 0 : pickupDiv + (m - 1) * barDiv; // m = 0 is the pickup
        int MeasureLen(int m) => m == 0 ? pickupDiv : barDiv;
        int MeasureOf(int t) => pickupDiv > 0 && t < pickupDiv ? 0 : (t - pickupDiv) / barDiv + 1;
        int firstMeasure = pickupDiv > 0 ? 0 : 1;

        // Split every note at bar lines → segments, per staff and measure.
        var segs = new Dictionary<(int staff, int measure), List<Seg>>();
        for (int i = 0; i < song.notes.Count; i++)
        {
            var n = song.notes[i];
            int staff = n.hand == PianoHand.Left ? 2 : 1;
            int start = Abs(n), end = start + Math.Max(1, (int)Math.Round(n.beats * beatDiv));
            int piece = 0;
            while (start < end)
            {
                int m = MeasureOf(start);
                if (m > bars) break;
                int mEnd = MeasureStart(m) + MeasureLen(m);
                int segEnd = Math.Min(end, mEnd);
                var s = new Seg { start = start, dur = segEnd - start, midi = n.midi, finger = n.finger, noteIndex = i, piece = piece, tieStop = piece > 0, tieStart = segEnd < end };
                if (!segs.TryGetValue((staff, m), out var list)) segs[(staff, m)] = list = new List<Seg>();
                list.Add(s);
                start = segEnd; piece++;
            }
        }

        bool flats = e.keyFifths < 0;
        double avgLh = song.notes.Where(n => n.hand == PianoHand.Left).Select(n => (double)n.midi).DefaultIfEmpty(48).Average();
        double avgRh = song.notes.Where(n => n.hand == PianoHand.Right).Select(n => (double)n.midi).DefaultIfEmpty(67).Average();

        // Clef per staff per measure (true = treble). Start from the hand's overall range; a measure whose notes sit far
        // outside it switches clef (≥ E4 average on the bass staff → treble, ≤ B3 average on the treble staff → bass),
        // and empty measures keep the previous clef.
        var clefs = new Dictionary<int, Dictionary<int, bool>> { [1] = new Dictionary<int, bool>(), [2] = new Dictionary<int, bool>() };
        for (int staff = 1; staff <= 2; staff++)
        {
            bool current = staff == 1 ? avgRh >= 55 : avgLh >= 60;
            for (int m = firstMeasure; m <= bars; m++)
            {
                if (segs.TryGetValue((staff, m), out var ms) && ms.Count > 0)
                {
                    double avg = ms.Average(s => s.midi);
                    if (current && avg <= 59) current = false;
                    else if (!current && avg >= 64) current = true;
                }
                clefs[staff][m] = current;
            }
            clefs[staff][firstMeasure - 1] = clefs[staff][firstMeasure];
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">");
        sb.AppendLine("<score-partwise version=\"4.0\">");
        sb.AppendLine($"  <work><work-title>{X(e.name)}</work-title></work>");
        sb.Append("  <identification>");
        if (!string.IsNullOrEmpty(e.composer)) sb.Append($"<creator type=\"composer\">{X(e.composer)}</creator>");
        if (!string.IsNullOrEmpty(e.arrangement) && e.arrangement != "as written") sb.Append($"<creator type=\"arranger\">{X(Capitalize(e.arrangement))}</creator>");
        sb.AppendLine("<encoding><software>ImmerseTheBay2026 song database</software></encoding></identification>");
        // Credits = what Verovio prints in the page header (title centered, composer/arranger right).
        // Tempo goes in the header (left) with the arrangement note: as a direction above the staff it collided with bar numbers.
        string unitWord = den == 8 ? "eighth" : den == 2 ? "half" : "quarter";
        string left = $"Tempo {e.tempoBpm} ({unitWord} notes per minute)" +
                      (!string.IsNullOrEmpty(e.arrangement) && e.arrangement != "as written" ? " · " + Capitalize(e.arrangement) : "");
        sb.AppendLine($"  <credit page=\"1\"><credit-type>title</credit-type><credit-words justify=\"center\" valign=\"top\" font-size=\"28\">{X(e.name)}</credit-words></credit>");
        if (!string.IsNullOrEmpty(e.composer))
            sb.AppendLine($"  <credit page=\"1\"><credit-type>composer</credit-type><credit-words justify=\"right\" valign=\"top\" font-size=\"11\">{X(e.composer)}</credit-words></credit>");
        sb.AppendLine($"  <credit page=\"1\"><credit-type>arranger</credit-type><credit-words justify=\"left\" valign=\"top\" font-size=\"10\">{X(left)}</credit-words></credit>");
        sb.AppendLine("  <part-list><score-part id=\"P1\"><part-name print-object=\"no\">Piano</part-name></score-part></part-list>");
        sb.AppendLine("  <part id=\"P1\">");

        for (int m = firstMeasure; m <= bars; m++)
        {
            int mStart = MeasureStart(m), mLen = MeasureLen(m);
            sb.Append(m == 0 ? "    <measure number=\"0\" implicit=\"yes\">\n" : $"    <measure number=\"{m}\">\n");
            if (m == firstMeasure)
            {
                sb.AppendLine("      <attributes>");
                sb.AppendLine($"        <divisions>{Div}</divisions>");
                sb.AppendLine($"        <key><fifths>{e.keyFifths}</fifths><mode>{(e.keyMode == "minor" ? "minor" : "major")}</mode></key>");
                sb.AppendLine($"        <time><beats>{e.beatsPerBar}</beats><beat-type>{den}</beat-type></time>");
                sb.AppendLine("        <staves>2</staves>");
                sb.AppendLine(clefs[1][m] ? "        <clef number=\"1\"><sign>G</sign><line>2</line></clef>" : "        <clef number=\"1\"><sign>F</sign><line>4</line></clef>");
                sb.AppendLine(clefs[2][m] ? "        <clef number=\"2\"><sign>G</sign><line>2</line></clef>" : "        <clef number=\"2\"><sign>F</sign><line>4</line></clef>");
                sb.AppendLine("      </attributes>");
                // Playback tempo only (the printed tempo is in the header: a metronome mark's note symbol needs a music
                // font the PNG renderer doesn't have, and words above the staff collided with bar numbers).
                double quarterBpm = e.tempoBpm * quartersPerBeat;
                sb.AppendLine($"      <sound tempo=\"{quarterBpm.ToString("0.##", CultureInfo.InvariantCulture)}\"/>");
            }
            else
            {
                // Clef changes when a hand moves far up/down (e.g. Für Elise bars 14–15, LH up to E5).
                var changes = new StringBuilder();
                for (int staff = 1; staff <= 2; staff++)
                    if (clefs[staff].TryGetValue(m, out bool trebleNow) && trebleNow != clefs[staff][m - 1])
                        changes.Append(trebleNow ? $"<clef number=\"{staff}\"><sign>G</sign><line>2</line></clef>" : $"<clef number=\"{staff}\"><sign>F</sign><line>4</line></clef>");
                if (changes.Length > 0) sb.AppendLine($"      <attributes>{changes}</attributes>");
            }

            bool firstVoiceWritten = false;
            for (int staff = 1; staff <= 2; staff++)
            {
                segs.TryGetValue((staff, m), out var list);
                var voices = AssignVoices(list ?? new List<Seg>());
                if (voices.Count == 0) voices.Add(new List<List<Seg>>()); // empty staff → one voice of rests
                for (int v = 0; v < voices.Count; v++)
                {
                    if (firstVoiceWritten) sb.AppendLine($"      <backup><duration>{mLen}</duration></backup>");
                    firstVoiceWritten = true;
                    int voiceNo = (staff - 1) * 4 + v + 1;
                    var events = BuildEvents(voices[v], mStart, mLen, fillRests: v == 0);
                    Beam(events, mStart, den, e.beatsPerBar);
                    var state = new Dictionary<(char, int), int>(); // displayed accidentals so far in this bar+staff
                    foreach (var ev in events)
                    {
                        if (ev.type == null) { sb.AppendLine($"      <forward><duration>{ev.dur}</duration><voice>{voiceNo}</voice><staff>{staff}</staff></forward>"); continue; }
                        if (ev.IsRest)
                        {
                            bool whole = ev.dur == mLen && m != 0;
                            sb.Append("      <note>").Append(whole ? "<rest measure=\"yes\"/>" : "<rest/>")
                              .Append($"<duration>{ev.dur}</duration><voice>{voiceNo}</voice>");
                            if (!whole) { sb.Append($"<type>{ev.type}</type>"); for (int d = 0; d < ev.dots; d++) sb.Append("<dot/>"); }
                            sb.Append($"<staff>{staff}</staff></note>\n");
                            continue;
                        }
                        for (int k = 0; k < ev.notes.Count; k++)
                        {
                            var s = ev.notes[k];
                            var (step, alter, octave) = Spell(s.midi, flats);
                            bool tieStop = ev.tieStop || (s.tieStop && ev.firstPiece);
                            bool tieStart = ev.tieStart || (s.tieStart && IsLastPiece(ev, s, events));
                            string id = ev.firstPiece ? s.Id : $"{s.Id}p{ev.start - s.start}";
                            sb.Append($"      <note id=\"{id}\">");
                            if (k > 0) sb.Append("<chord/>");
                            sb.Append($"<pitch><step>{step}</step>");
                            if (alter != 0) sb.Append($"<alter>{alter}</alter>");
                            sb.Append($"<octave>{octave}</octave></pitch><duration>{ev.dur}</duration>");
                            if (tieStop) sb.Append("<tie type=\"stop\"/>");
                            if (tieStart) sb.Append("<tie type=\"start\"/>");
                            sb.Append($"<voice>{voiceNo}</voice><type>{ev.type}</type>");
                            for (int d = 0; d < ev.dots; d++) sb.Append("<dot/>");
                            string acc = DisplayedAccidental(step[0], octave, alter, e.keyFifths, state, tieStop);
                            if (acc != null) sb.Append($"<accidental>{acc}</accidental>");
                            sb.Append($"<staff>{staff}</staff>");
                            if (k == 0)
                                for (int lvl = 0; lvl < 4; lvl++)
                                    if (ev.beams[lvl] != null) sb.Append($"<beam number=\"{lvl + 1}\">{ev.beams[lvl]}</beam>");
                            bool finger = IncludeFingering && ev.firstPiece && s.piece == 0 && s.finger > 0;
                            if (tieStop || tieStart || finger)
                            {
                                sb.Append("<notations>");
                                if (tieStop) sb.Append("<tied type=\"stop\"/>");
                                if (tieStart) sb.Append("<tied type=\"start\"/>");
                                if (finger) sb.Append($"<technical><fingering>{s.finger}</fingering></technical>");
                                sb.Append("</notations>");
                            }
                            sb.Append("</note>\n");
                        }
                    }
                }
            }
            if (m == bars) sb.AppendLine("      <barline location=\"right\"><bar-style>light-heavy</bar-style></barline>");
            sb.AppendLine("    </measure>");
        }
        sb.AppendLine("  </part>");
        sb.AppendLine("</score-partwise>");
        return new Result { xml = sb.ToString(), song = song, hasPickup = pickupDiv > 0, barCount = bars };
    }

    // ---------- Voices, events, beams ----------

    // Chords = same start + same length. Greedy: each chord goes to the first voice that is free at its start.
    static List<List<List<Seg>>> AssignVoices(List<Seg> segs)
    {
        var chords = segs.GroupBy(s => (s.start, s.dur)).Select(g => g.OrderBy(s => s.midi).ToList())
                         .OrderBy(c => c[0].start).ThenByDescending(c => c[0].dur).ToList();
        var voices = new List<List<List<Seg>>>();
        var ends = new List<int>();
        foreach (var c in chords)
        {
            int v = ends.FindIndex(end => end <= c[0].start);
            if (v < 0) { voices.Add(new List<List<Seg>>()); ends.Add(0); v = voices.Count - 1; }
            voices[v].Add(c);
            ends[v] = c[0].start + c[0].dur;
        }
        return voices;
    }

    static List<Ev> BuildEvents(List<List<Seg>> chords, int mStart, int mLen, bool fillRests)
    {
        var events = new List<Ev>();
        int t = mStart, mEnd = mStart + mLen;
        void Gap(int until)
        {
            if (until <= t) return;
            if (fillRests) foreach (var (s, d, type, dots) in Split(t, until - t)) events.Add(new Ev { start = s, dur = d, type = type, dots = dots });
            else events.Add(new Ev { start = t, dur = until - t, type = null }); // <forward>: invisible spacer in extra voices
            t = until;
        }
        foreach (var c in chords)
        {
            Gap(c[0].start);
            var pieces = Split(c[0].start, c[0].dur);
            for (int p = 0; p < pieces.Count; p++)
            {
                var (s, d, type, dots) = pieces[p];
                events.Add(new Ev
                {
                    start = s, dur = d, type = type, dots = dots, notes = c, firstPiece = p == 0,
                    tieStop = p > 0, tieStart = p < pieces.Count - 1,
                });
            }
            t = c[0].start + c[0].dur;
        }
        Gap(mEnd);
        return events;
    }

    static bool IsLastPiece(Ev ev, Seg s, List<Ev> events) => !events.Any(o => o != ev && o.notes == ev.notes && o.start > ev.start);

    // Length in divisions → printable pieces (longest first).
    static List<(int start, int dur, string type, int dots)> Split(int start, int dur)
    {
        var res = new List<(int, int, string, int)>();
        int left = dur;
        while (left > 0)
        {
            var pick = Lengths.FirstOrDefault(l => (int)Math.Round(l.q * Div) <= left);
            int d = pick.type == null ? left : (int)Math.Round(pick.q * Div);
            res.Add((start, d, pick.type ?? "64th", pick.type == null ? 0 : pick.dots));
            start += d; left -= d;
        }
        return res;
    }

    // Beam eighth notes and shorter within each beat group (a dotted quarter in x/8 time with 3, 6, 9 or 12 beats, else a quarter).
    static void Beam(List<Ev> events, int mStart, int den, int beats)
    {
        int group = den == 8 && beats % 3 == 0 ? (int)(Div * 1.5) : den == 8 ? Div / 2 : Div;
        int Level(Ev ev) => ev.IsRest || ev.type == null ? 0 : ev.type == "eighth" ? 1 : ev.type == "16th" ? 2 : ev.type == "32nd" ? 3 : ev.type == "64th" ? 4 : 0;
        foreach (var g in events.Where(ev => Level(ev) > 0).GroupBy(ev => (ev.start - mStart) / group))
        {
            var run = g.OrderBy(ev => ev.start).ToList();
            // Only consecutive notes (no rest/gap between) share a beam.
            var chunks = new List<List<Ev>>();
            foreach (var ev in run)
            {
                if (chunks.Count == 0 || chunks[chunks.Count - 1].Last().start + chunks[chunks.Count - 1].Last().dur != ev.start) chunks.Add(new List<Ev>());
                chunks[chunks.Count - 1].Add(ev);
            }
            foreach (var chunk in chunks.Where(c => c.Count >= 2))
                for (int lvl = 1; lvl <= 4; lvl++)
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (Level(chunk[i]) < lvl) continue;
                        bool prev = i > 0 && Level(chunk[i - 1]) >= lvl, next = i < chunk.Count - 1 && Level(chunk[i + 1]) >= lvl;
                        chunk[i].beams[lvl - 1] = lvl == 1 ? (i == 0 ? "begin" : i == chunk.Count - 1 ? "end" : "continue")
                            : prev && next ? "continue" : prev ? "end" : next ? "begin" : (i == 0 ? "forward hook" : "backward hook");
                    }
        }
    }

    // ---------- Pitch spelling + accidentals ----------

    static (string step, int alter, int octave) Spell(int midi, bool flats)
    {
        string name = (flats ? FlatNames : SharpNames)[((midi % 12) + 12) % 12];
        int alter = name.Length > 1 ? (name[1] == '#' ? 1 : -1) : 0;
        return (name.Substring(0, 1), alter, midi / 12 - 1);
    }

    static int KeyAlter(char step, int fifths) =>
        fifths > 0 && SharpOrder.IndexOf(step) < fifths ? 1 : fifths < 0 && FlatOrder.IndexOf(step) < -fifths ? -1 : 0;

    static string DisplayedAccidental(char step, int octave, int alter, int fifths, Dictionary<(char, int), int> state, bool tieStop)
    {
        int expected = state.TryGetValue((step, octave), out var a) ? a : KeyAlter(step, fifths);
        state[(step, octave)] = alter;
        if (tieStop || alter == expected) return null;
        return alter == 1 ? "sharp" : alter == -1 ? "flat" : "natural";
    }

    // ---------- Helpers ----------

    public static int BeatUnitDenominator(string unit)
    {
        switch ((unit ?? "quarter").Trim().ToLowerInvariant())
        {
            case "whole": return 1;
            case "half": return 2;
            case "eighth": return 8;
            case "16th": case "sixteenth": return 16;
            case "quarter": return 4;
            default:
                if (unit.StartsWith("1/") && int.TryParse(unit.Substring(2), out int d)) return d;
                return 4;
        }
    }

    static string X(string s) => SecurityElement.Escape(s ?? "");
    static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
