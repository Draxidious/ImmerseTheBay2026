using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Connects the song database (Assets/Resources/SongDB) and the generated sheet music (Assets/Resources/Sheets) to Main's
/// lesson: the evaluator plays a database section, the AI coach is told about that same section, and the sheet panel
/// (SongNoteDisplay) shows the generated PNG strips. Every method returns null/false when the data isn't there, so callers
/// can fall back to their old behaviour.
/// </summary>
public static class LessonSongSource
{
    /// <summary>Bars of a named section ("A section (bars 1–8)"); empty section name = the whole song. Null if unknown.</summary>
    public static (int first, int last)? SectionBars(string songId, string section)
    {
        var entry = SongDatabase.Entry(songId);
        if (entry == null) return null;
        if (string.IsNullOrEmpty(section)) return (1, SongDatabase.Get(songId).BarCount);
        var s = entry.sections.FirstOrDefault(x => string.Equals(x.name, section, System.StringComparison.OrdinalIgnoreCase));
        return s == null ? ((int, int)?)null : (s.firstBar, s.lastBar);
    }

    /// <summary>
    /// The song the AI coach is told about: exactly what the student plays (the section, one hand), from the database.
    /// Null if the song or section isn't in the database.
    /// </summary>
    public static SongData ForCoach(string songId, string section, PianoHand hand = PianoHand.Right)
    {
        var bars = SectionBars(songId, section);
        if (bars == null) return null;
        var song = SongDatabase.Get(songId, bars.Value.first, bars.Value.last, hand);
        var entry = SongDatabase.Entry(songId);
        if (song != null && entry != null)
            song.timeSignature = $"{entry.beatsPerBar}/{MusicXmlDenominator(entry.beatUnit)}";
        return song;
    }

    /// <summary>
    /// The section as the lesson evaluator's monophonic LessonSong: one hand, one note per moment (the top note of a chord),
    /// beats counted from the section's first note, the pickup in bar 1 (same shape as SongLibrary.FurElise).
    /// </summary>
    public static LessonSong ForLesson(string songId, string section, float bpm, PianoHand hand = PianoHand.Right)
    {
        var song = ForCoach(songId, section, hand);
        var entry = SongDatabase.Entry(songId);
        if (song == null || entry == null || song.notes.Count == 0) return null;

        float Abs(SongNote n) => (n.bar - 1) * song.beatsPerBar + (n.beat - 1f);
        var notes = song.notes.GroupBy(Abs).OrderBy(g => g.Key).Select(g => g.OrderByDescending(n => n.midi).First()).ToList();
        float start = Abs(notes[0]);

        var lesson = new LessonSong
        {
            name = entry.name, bpm = bpm, beatsPerBar = entry.beatsPerBar,
            beatUnit = (string.IsNullOrEmpty(entry.beatUnit) ? "quarter" : entry.beatUnit) + " notes",
        };
        foreach (var n in notes)
            lesson.notes.Add(new LessonNote { midi = n.midi, bar = n.bar, beat = Abs(n) - start, duration = n.beats });
        return lesson;
    }

    /// <summary>
    /// Points SongNoteDisplay at the generated strips (one per line of music) that contain the section's bars, with the
    /// bar edges and highlight box from layout.json. Returns false (and leaves the display as it was) if there are none.
    /// </summary>
    public static bool ApplySheets(SongNoteDisplay display, string songId, string section)
    {
        if (!display) return false;
        var bars = SectionBars(songId, section);
        var sheet = SheetLibrary.Get(songId);
        if (bars == null || sheet == null || sheet.layout.systems == null || sheet.layout.systems.Count == 0) return false;

        var lines = new List<SheetSystem>();
        SheetSystemInfo firstLine = null;
        for (int i = 0; i < sheet.layout.systems.Count; i++)
        {
            var s = sheet.layout.systems[i];
            if (s.lastBar < bars.Value.first || s.firstBar > bars.Value.last) continue;
            if (i >= sheet.systems.Length || sheet.systems[i] == null) return false;
            lines.Add(new SheetSystem { image = sheet.systems[i], firstBar = s.firstBar, barEdges = s.barEdges, top = s.top, bottom = s.bottom });
            if (firstLine == null) firstLine = s;
        }
        if (lines.Count == 0) return false;

        display.sheet = lines;
        display.sheetAspect = firstLine.width / (float)Mathf.Max(1, firstLine.height); // all strips of a song share one size
        Debug.Log($"[LessonSongSource] Sheet panel: {lines.Count} line(s) of {songId} (bars {bars.Value.first}–{bars.Value.last}) from Assets/Resources/Sheets.");
        return true;
    }

    static int MusicXmlDenominator(string unit)
    {
        switch ((unit ?? "quarter").Trim().ToLowerInvariant())
        {
            case "half": return 2;
            case "eighth": return 8;
            case "16th": case "sixteenth": return 16;
            default: return 4;
        }
    }
}
