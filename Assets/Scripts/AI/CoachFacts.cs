using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// Musical facts worked out in code and handed to the coach, so the model explains instead of guessing:
// how each note moves from the one before it, a fingering for each bar, where a wrong key was relative to
// the right one, and what actually went well in the run.
public static class CoachFacts
{
    const int MaxBars = 8;
    const int NotableHesitationMs = 1500;

    static readonly int[] LetterOfPitchClass = { 0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6 }; // C C# D D# E F F# G G# A A# B
    static readonly string[] IntervalNames = { "", "step", "third", "fourth", "fifth", "sixth", "seventh", "octave" };
    static readonly string[] FingerNames = { "", "thumb", "index finger", "middle finger", "ring finger", "little finger" };

    public static bool IsBlack(int midi)
    {
        int pc = ((midi % 12) + 12) % 12;
        return pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10;
    }

    // Position counted in letter names (white keys), so a step is always 1 whatever the key signature.
    static int Diatonic(int midi) => (midi / 12) * 7 + LetterOfPitchClass[((midi % 12) + 12) % 12];

    public static string FingerName(int finger) => FingerNames[Math.Max(1, Math.Min(5, finger))];

    static List<SongNote> NotesIn(SongData song, PianoHand hand, int firstBar, int lastBar)
    {
        return song.notes.Where(n => n.hand == hand && n.bar >= firstBar && n.bar <= lastBar)
            .OrderBy(n => n.bar).ThenBy(n => n.beat).ThenBy(n => n.midi).ToList();
    }

    // "same note", "up a step", "down a third"...
    public static string Motion(int fromMidi, int toMidi)
    {
        if (fromMidi == toMidi) return "same note";
        int steps = Math.Abs(Diatonic(toMidi) - Diatonic(fromMidi));
        string dir = toMidi > fromMidi ? "up" : "down";
        if (steps == 0) return dir + " a half step";               // e.g. F to F#
        if (steps < IntervalNames.Length) return dir + " a" + (steps == 7 ? "n " : " ") + IntervalNames[steps];
        return dir + " more than an octave";
    }

    // Where a wrong key was, seen from the right one: "1 key too low, a black key".
    public static string PlayedIs(int expectedMidi, int playedMidi)
    {
        int keys = Math.Abs(playedMidi - expectedMidi);
        if (keys == 0) return "the right key";
        return keys + (keys == 1 ? " key" : " keys") + (playedMidi > expectedMidi ? " too high" : " too low") +
               (IsBlack(playedMidi) ? ", a black key" : ", a white key");
    }

    // One finger per note of that hand in the bar, in order (1 = thumb). Null when no safe fingering is known.
    // Uses SongNote.finger when the song provides it. Otherwise it only handles a hand that stays in one
    // five-finger position (lowest note = thumb for the right hand, highest note = thumb for the left).
    public static int[] Fingering(SongData song, int bar, PianoHand hand, SectionInfo section)
    {
        if (song == null) return null;
        var barNotes = NotesIn(song, hand, bar, bar);
        if (barNotes.Count == 0) return null;

        if (barNotes.All(n => n.finger >= 1 && n.finger <= 5)) return barNotes.Select(n => n.finger).ToArray();

        // Keep the hand where it is for the whole section if the section fits under five fingers, else just this bar.
        var position = barNotes;
        if (section != null && bar >= section.firstBar && bar <= section.lastBar)
        {
            var sectionNotes = NotesIn(song, hand, section.firstBar, section.lastBar);
            if (Span(sectionNotes) <= 4) position = sectionNotes;
        }
        if (Span(position) > 4) return null;

        int low = position.Min(n => Diatonic(n.midi));
        int high = position.Max(n => Diatonic(n.midi));
        return barNotes.Select(n => hand == PianoHand.Right ? Diatonic(n.midi) - low + 1 : high - Diatonic(n.midi) + 1).ToArray();
    }

    static int Span(List<SongNote> notes) => notes.Count == 0 ? 0 : notes.Max(n => Diatonic(n.midi)) - notes.Min(n => Diatonic(n.midi));

    // Things the run data supports praising. Empty when there is nothing specific.
    public static List<string> WentWell(CoachContext ctx)
    {
        var good = new List<string>();
        var run = ctx.thisRun;
        var section = ctx.currentSection;
        if (run == null || section == null || ctx.trigger == CoachTrigger.LessonStart) return good;

        var wrongBars = new HashSet<int>((run.wrongNotes ?? new List<WrongNote>()).Select(w => w.bar));
        var slowBars = new HashSet<int>((run.hesitations ?? new List<Hesitation>()).Where(h => h.ms >= NotableHesitationMs).Select(h => h.bar));
        var rushed = new HashSet<int>((run.rushedBars ?? new List<int>()).Concat(run.offBeatBars ?? new List<int>()));
        bool timing = run.timingChecked;
        string clean = timing ? "right notes, no long pauses, steady timing" : "right notes, no long pauses";

        var cleanBars = new List<int>();
        for (int bar = section.firstBar; bar <= section.lastBar; bar++)
            if (!wrongBars.Contains(bar) && !slowBars.Contains(bar) && !rushed.Contains(bar)) cleanBars.Add(bar);

        int total = section.lastBar - section.firstBar + 1;
        if (cleanBars.Count == total && total > 0) good.Add("every bar was clean: " + clean);
        else if (cleanBars.Count > 0) good.Add("clean bars (" + clean + "): " + string.Join(", ", cleanBars));

        if (wrongBars.Count == 0 && cleanBars.Count != total) good.Add("every note was right");
        if (timing && rushed.Count == 0 && slowBars.Count == 0 && cleanBars.Count != total) good.Add("steady timing all the way through");

        var trend = ctx.history?.accuracyTrend;
        if (trend != null && trend.Count >= 2 && trend[trend.Count - 1] > trend[trend.Count - 2] && trend[trend.Count - 1] > trend[0])
            good.Add("more accurate than the earlier attempts");

        return good;
    }

    // ,"facts":{...} for the context JSON. Empty string when there is no song to work from.
    public static string ToJson(CoachContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("\"went_well\":[");
        sb.Append(string.Join(",", WentWell(ctx).Select(AIHttp.Quote)));
        sb.Append(']');

        var song = ctx.song;
        var section = ctx.currentSection;
        if (song != null && section != null)
        {
            int first = Math.Max(1, section.firstBar);
            int last = Math.Min(Math.Max(first, section.lastBar), first + MaxBars - 1);

            sb.Append(",\"bars\":[");
            bool any = false;
            foreach (var hand in new[] { PianoHand.Right, PianoHand.Left })
            {
                for (int bar = first; bar <= last; bar++)
                {
                    var notes = NotesIn(song, hand, bar, bar);
                    if (notes.Count == 0) continue;

                    if (any) sb.Append(',');
                    any = true;
                    sb.Append("{\"bar\":").Append(bar).Append(",\"hand\":").Append(hand == PianoHand.Right ? "\"right\"" : "\"left\"");
                    sb.Append(",\"notes\":").Append(AIHttp.Quote(DescribeBar(song, hand, bar, notes)));

                    int[] fingers = Fingering(song, bar, hand, section);
                    if (fingers != null)
                        sb.Append(",\"fingering\":").Append(AIHttp.Quote(string.Join(", ", notes.Select((n, i) => SongFormatter.NoteName(n.midi) + " " + FingerName(fingers[i])))));
                    var hints = notes.Where(n => !string.IsNullOrEmpty(n.hint)).Select(n => SongFormatter.NoteName(n.midi) + ": " + n.hint).ToList();
                    if (hints.Count > 0) sb.Append(",\"hints\":").Append(AIHttp.Quote(string.Join("; ", hints)));
                    sb.Append('}');
                }
            }
            sb.Append(']');
        }

        return ",\"facts\":{" + sb + "}";
    }

    // "C4, C4 (same note), D4 (up a step), E4 (up a step)". The first note is compared with the last note of the bar before.
    static string DescribeBar(SongData song, PianoHand hand, int bar, List<SongNote> notes)
    {
        var previous = NotesIn(song, hand, bar - 1, bar - 1).LastOrDefault();
        var sb = new StringBuilder();
        float beat = float.NaN;
        for (int i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            if (i > 0) sb.Append(n.beat == beat ? " + " : ", ");
            sb.Append(SongFormatter.NoteName(n.midi));

            bool chordTone = i > 0 && n.beat == beat; // upper notes of a chord are not a melodic move
            var from = i > 0 ? notes[i - 1] : previous;
            if (from != null && !chordTone) sb.Append(" (").Append(Motion(from.midi, n.midi)).Append(i == 0 ? " from the bar before" : "").Append(')');
            beat = n.beat;
        }
        return sb.ToString();
    }

    // How to find the note someone is stuck on, from the note before it.
    public static string HowToFind(SongData song, int bar, int midi)
    {
        string key = IsBlack(midi) ? "a black key" : "a white key";
        if (song == null) return key;

        foreach (var hand in new[] { PianoHand.Right, PianoHand.Left })
        {
            var notes = NotesIn(song, hand, Math.Max(1, bar - 1), bar);
            int index = notes.FindIndex(n => n.bar == bar && n.midi == midi);
            if (index < 0) continue;

            // Skip back over repeats of the same pitch to the last different note.
            int back = index - 1;
            while (back >= 0 && notes[back].midi == midi) back--;
            string hint = key + ", " + (hand == PianoHand.Right ? "right hand" : "left hand");
            if (back >= 0) hint += ", " + Motion(notes[back].midi, midi) + " from " + SongFormatter.NoteName(notes[back].midi);
            if (!string.IsNullOrEmpty(notes[index].hint)) hint += ", " + notes[index].hint;
            return hint;
        }
        return key;
    }
}
