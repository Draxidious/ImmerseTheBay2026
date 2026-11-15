using System;
using System.Collections.Generic;

/// <summary>One note of a song. Notes are MIDI note numbers (middle C = 60).</summary>
[Serializable]
public class LessonNote
{
    public int midi;
    public float beat;     // when the note starts, in beats from the start of the song
    public float duration; // in beats
    public int bar;        // 1-based

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static string NameOf(int midi) => Names[((midi % 12) + 12) % 12] + (midi / 12 - 1);
    public string Name => NameOf(midi);
}

/// <summary>A monophonic line to play, with the tempo it should be played at.</summary>
[Serializable]
public class LessonSong
{
    public string name;
    public float bpm;          // target tempo in beats per minute
    public string beatUnit;    // what one beat is, for display ("eighth notes")
    public int beatsPerBar;
    public List<LessonNote> notes = new List<LessonNote>();

    public float TotalBeats => notes.Count == 0 ? 0f : notes[notes.Count - 1].beat + notes[notes.Count - 1].duration;
}

public static class SongLibrary
{
    /// <summary>
    /// Opening theme of Beethoven's "Für Elise" (right hand, first 8 bars with the pickup). 3/8 time, one beat = an eighth note.
    /// </summary>
    public static LessonSong FurElise(float bpm = 90f)
    {
        const int E5 = 76, Ds5 = 75, D5 = 74, C5 = 72, B4 = 71, A4 = 69, Gs4 = 68, E4 = 64, C4 = 60;

        // (midi, start in sixteenths, length in sixteenths). The pickup is 2 sixteenths, so bar 1 starts at 2.
        var raw = new (int midi, int start, int len)[]
        {
            (E5, 0, 1), (Ds5, 1, 1),
            (E5, 2, 1), (Ds5, 3, 1), (E5, 4, 1), (B4, 5, 1), (D5, 6, 1), (C5, 7, 1),       // bar 1
            (A4, 8, 2), (C4, 11, 1), (E4, 12, 1), (A4, 13, 1),                             // bar 2
            (B4, 14, 2), (E4, 17, 1), (Gs4, 18, 1), (B4, 19, 1),                           // bar 3
            (C5, 20, 2), (E4, 23, 1), (E5, 24, 1), (Ds5, 25, 1),                           // bar 4
            (E5, 26, 1), (Ds5, 27, 1), (E5, 28, 1), (B4, 29, 1), (D5, 30, 1), (C5, 31, 1), // bar 5
            (A4, 32, 2), (C4, 35, 1), (E4, 36, 1), (A4, 37, 1),                            // bar 6
            (B4, 38, 2), (E4, 41, 1), (C5, 42, 1), (B4, 43, 1),                            // bar 7
            (A4, 44, 4),                                                                   // bar 8
        };

        var song = new LessonSong { name = "Für Elise", bpm = bpm, beatUnit = "eighth notes", beatsPerBar = 3 };
        foreach (var (midi, start, len) in raw)
        {
            song.notes.Add(new LessonNote
            {
                midi = midi,
                beat = start / 2f,
                duration = len / 2f,
                bar = start < 2 ? 1 : (start - 2) / 6 + 1, // the pickup counts as bar 1
            });
        }
        return song;
    }
}
