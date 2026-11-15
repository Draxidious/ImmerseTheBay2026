using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Parses the song notation used in <see cref="SongEntry"/> bar strings, converts entries to the AI layer's
/// <see cref="SongData"/>, and validates songs. Plain C#, no Unity calls, so it can run anywhere.
/// </summary>
public static class SongNotation
{
    public const int LowestKey = 21;   // A0, lowest key on an 88-key piano (Yamaha P71)
    public const int HighestKey = 108; // C8

    static readonly Regex NoteRx = new Regex(@"^([A-Ga-g])([#b]?)(-?\d)(?:@([1-5]))?$", RegexOptions.Compiled);
    static readonly int[] PitchClass = { 9, 11, 0, 2, 4, 5, 7 }; // A B C D E F G

    public struct Event
    {
        public float offset;   // beats from the start of the bar string (0-based)
        public float beats;
        public List<(int midi, int finger)> notes; // empty = rest
    }

    /// <summary>MIDI number for a note name like "C4", "F#3", "Bb5" (middle C = C4 = 60). -1 if invalid.</summary>
    public static int MidiOf(string name)
    {
        var m = NoteRx.Match(name.Trim());
        if (!m.Success) return -1;
        int pc = PitchClass[char.ToUpperInvariant(m.Groups[1].Value[0]) - 'A'];
        if (m.Groups[2].Value == "#") pc++;
        else if (m.Groups[2].Value == "b") pc--;
        int octave = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return (octave + 1) * 12 + pc;
    }

    /// <summary>Parse one bar string ("E4 D4 C4:2", "C4+E4:0.5", "r:1", "E4@3") into timed events.</summary>
    public static List<Event> ParseBar(string bar, List<string> errors = null, string where = "")
    {
        var events = new List<Event>();
        if (string.IsNullOrWhiteSpace(bar)) return events;
        float offset = 0f;
        foreach (string token in bar.Split(new[] { ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string body = token;
            float beats = 1f;
            int colon = token.LastIndexOf(':');
            if (colon >= 0)
            {
                body = token.Substring(0, colon);
                if (!float.TryParse(token.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out beats) || beats <= 0f)
                {
                    errors?.Add($"{where}: bad length in \"{token}\"");
                    beats = 1f;
                }
            }

            var ev = new Event { offset = offset, beats = beats, notes = new List<(int, int)>() };
            if (!body.Equals("r", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string part in body.Split('+'))
                {
                    var m = NoteRx.Match(part);
                    int midi = MidiOf(part);
                    if (!m.Success || midi < 0) { errors?.Add($"{where}: can't read note \"{part}\" in \"{token}\""); continue; }
                    int finger = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
                    ev.notes.Add((midi, finger));
                }
            }
            events.Add(ev);
            offset += beats;
        }
        return events;
    }

    static float BarLength(string bar) => ParseBar(bar).Sum(e => e.beats);

    /// <summary>Convert to the AI layer's SongData (all bars, both hands).</summary>
    public static SongData ToSongData(SongEntry e) => ToSongData(e, 0, 0, null);

    /// <summary>
    /// Convert to SongData, optionally only bars firstBar..lastBar (0 = no limit) and one hand (null = both).
    /// Bar numbers are kept as in the sheet music (a section starting at bar 9 still says bar 9).
    /// </summary>
    public static SongData ToSongData(SongEntry e, int firstBar, int lastBar, PianoHand? onlyHand)
    {
        var song = new SongData
        {
            id = e.id,
            name = e.name,
            tempoBpm = e.tempoBpm,
            beatsPerBar = e.beatsPerBar,
        };
        if (firstBar > 0 || lastBar > 0 || onlyHand != null)
        {
            // Different id, so SongFormatter's per-id cache doesn't mix the section up with the whole song.
            song.id = $"{e.id}#{firstBar}-{lastBar}{(onlyHand == null ? "" : onlyHand == PianoHand.Right ? "R" : "L")}";
        }

        AddHand(song, e.pickupRH, e.rh, PianoHand.Right, e.beatsPerBar);
        AddHand(song, e.pickupLH, e.lh, PianoHand.Left, e.beatsPerBar);
        foreach (var n in e.notes)
        {
            song.notes.Add(new SongNote
            {
                midi = n.midi, bar = n.bar, beat = n.beat, beats = n.beats, finger = n.finger,
                hand = n.hand != null && n.hand.StartsWith("L", StringComparison.OrdinalIgnoreCase) ? PianoHand.Left : PianoHand.Right,
            });
        }

        song.notes = song.notes
            .Where(n => (firstBar <= 0 || n.bar >= firstBar) && (lastBar <= 0 || n.bar <= lastBar))
            .Where(n => onlyHand == null || n.hand == onlyHand.Value)
            .OrderBy(n => n.bar).ThenBy(n => n.beat).ThenBy(n => n.hand).ThenBy(n => n.midi)
            .ToList();
        return song;
    }

    static void AddHand(SongData song, string pickup, List<string> bars, PianoHand hand, int beatsPerBar)
    {
        // Pickup notes belong to bar 1 and come before its first beat (beat < 1), like Jordan's Für Elise.
        if (!string.IsNullOrWhiteSpace(pickup))
        {
            float len = BarLength(pickup);
            foreach (var ev in ParseBar(pickup))
                foreach (var (midi, finger) in ev.notes)
                    song.notes.Add(new SongNote { midi = midi, bar = 1, beat = 1f - len + ev.offset, beats = ev.beats, hand = hand, finger = finger });
        }
        if (bars == null) return;
        for (int i = 0; i < bars.Count; i++)
            foreach (var ev in ParseBar(bars[i]))
                foreach (var (midi, finger) in ev.notes)
                    song.notes.Add(new SongNote { midi = midi, bar = i + 1, beat = 1f + ev.offset, beats = ev.beats, hand = hand, finger = finger });
    }

    /// <summary>
    /// Checks a song for mistakes a typo would cause. Errors = definitely wrong; warnings = worth a look.
    /// Note: this can't tell whether the notes match the real piece — that needs a person with the sheet music.
    /// </summary>
    public static void Validate(SongEntry e, List<string> errors, List<string> warnings)
    {
        string id = string.IsNullOrEmpty(e.id) ? "(no id)" : e.id;
        if (string.IsNullOrWhiteSpace(e.id)) errors.Add($"{id}: missing id");
        if (string.IsNullOrWhiteSpace(e.name)) warnings.Add($"{id}: missing name");
        if (e.beatsPerBar <= 0) errors.Add($"{id}: beatsPerBar must be > 0");
        if (e.tempoBpm <= 0) errors.Add($"{id}: tempoBpm must be > 0");

        // Notation readable + bar lengths add up.
        CheckBars(e, e.rh, e.pickupRH, "RH", errors, warnings);
        CheckBars(e, e.lh, e.pickupLH, "LH", errors, warnings);
        if (e.rh.Count > 0 && e.lh.Count > 0 && e.rh.Count != e.lh.Count)
            warnings.Add($"{id}: RH has {e.rh.Count} bars but LH has {e.lh.Count}");

        var song = ToSongData(e);
        if (song.notes.Count == 0) errors.Add($"{id}: no notes");
        int bars = song.BarCount;
        foreach (var n in song.notes)
        {
            string at = $"{id} bar {n.bar} beat {n.beat:0.##} {SongFormatter.NoteName(n.midi)}";
            if (n.midi < LowestKey || n.midi > HighestKey) errors.Add($"{at}: outside the 88 keys (A0–C8)");
            if (n.beats <= 0f) errors.Add($"{at}: length must be > 0");
            if (n.bar < 1) errors.Add($"{at}: bars start at 1");
            bool pickup = n.bar == 1 && n.beat < 1f;
            if (!pickup && (n.beat < 1f || n.beat >= e.beatsPerBar + 1f)) errors.Add($"{at}: beat outside the bar (1..{e.beatsPerBar + 1})");
            if (n.finger < 0 || n.finger > 5) errors.Add($"{at}: finger must be 0–5");
        }

        // Same key struck again before it was released, in the same hand (usually a typo or a missing rest).
        foreach (var group in song.notes.GroupBy(n => (n.hand, n.midi)))
        {
            var list = group.OrderBy(n => Abs(n, e.beatsPerBar)).ToList();
            for (int i = 1; i < list.Count; i++)
            {
                float prevEnd = Abs(list[i - 1], e.beatsPerBar) + list[i - 1].beats;
                if (Abs(list[i], e.beatsPerBar) < prevEnd - 0.001f)
                    warnings.Add($"{id} bar {list[i].bar} beat {list[i].beat:0.##} {SongFormatter.NoteName(list[i].midi)} ({group.Key.hand}): starts before the same key from bar {list[i - 1].bar} ends");
            }
        }

        foreach (var s in e.sections)
            if (s.firstBar < 1 || s.lastBar < s.firstBar || (bars > 0 && s.lastBar > bars))
                errors.Add($"{id}: section \"{s.name}\" bars {s.firstBar}–{s.lastBar} don't fit the song (1–{bars})");
    }

    static float Abs(SongNote n, int beatsPerBar) => (n.bar - 1) * beatsPerBar + (n.beat - 1f);

    static void CheckBars(SongEntry e, List<string> bars, string pickup, string hand, List<string> errors, List<string> warnings)
    {
        string id = e.id;
        if (!string.IsNullOrWhiteSpace(pickup))
        {
            ParseBar(pickup, errors, $"{id} {hand} pickup");
            float p = BarLength(pickup);
            if (p >= e.beatsPerBar) warnings.Add($"{id} {hand} pickup: {p:0.##} beats is a whole bar or more");
        }
        for (int i = 0; i < bars.Count; i++)
        {
            ParseBar(bars[i], errors, $"{id} {hand} bar {i + 1}");
            float len = BarLength(bars[i]);
            if (Math.Abs(len - e.beatsPerBar) > 0.001f)
                warnings.Add($"{id} {hand} bar {i + 1}: {len:0.##} beats, expected {e.beatsPerBar} (\"{bars[i]}\")");
        }
    }
}
