using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The pieces the curriculum teaches, loaded from Assets/Resources/Songs/<id>.json, plus the conversion to ExpectedNote.
// The files hold every note with its finger and an optional hint, so fingerings can be corrected without code.
// There is no MIDI parser in the app: fur_elise.json was converted once from a MIDI file (both hands, all 105 bars).
// Bar numbering follows the sheet images: the E-D# pickup counts as bar 1, the first and second endings are bars 8 and 9.
public static class CurriculumSongs
{
    public const string ScaleId = "c_major_scale";
    public const string FurEliseId = "fur_elise";

    [Serializable]
    class SongFile
    {
        public string id;
        public string name;
        public int tempoBpm = 100;
        public string timeSignature;
        public int beatsPerBar = 4;
        public List<NoteLine> notes = new List<NoteLine>();
    }

    [Serializable]
    class NoteLine
    {
        public string note;      // "E5", "D#5"
        public int bar;
        public float beat = 1f;
        public float beats = 1f;
        public int finger;
        public string hint;
        public string hand;      // "L" or "left" = left hand; anything else (or nothing) = right hand
    }

    static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    // C major scale, C4 to C5 and back, right hand. bpm replaces the file's tempo.
    public static SongData CMajorScale(int bpm)
    {
        var song = Load(ScaleId);
        song.tempoBpm = bpm;
        return song;
    }

    // Beethoven's "Für Elise", both hands, up to the given number of bars (the file has 105).
    public static SongData FurElise(int bars = 8)
    {
        var song = Load(FurEliseId);
        song.notes.RemoveAll(n => n.bar > bars);
        return song;
    }

    // Reads Assets/Resources/Songs/<id>.json. Returns an empty song (and logs an error) if the file is missing or bad.
    public static SongData Load(string id)
    {
        var song = new SongData { id = id, name = id };
        var asset = Resources.Load<TextAsset>("Songs/" + id);
        if (asset == null)
        {
            Debug.LogError($"[Lesson] Song file Assets/Resources/Songs/{id}.json not found.");
            return song;
        }

        try
        {
            var file = JsonUtility.FromJson<SongFile>(asset.text);
            song.name = string.IsNullOrEmpty(file.name) ? id : file.name;
            song.tempoBpm = file.tempoBpm;
            song.beatsPerBar = file.beatsPerBar;
            song.timeSignature = file.timeSignature;
            foreach (var line in file.notes)
            {
                int midi = ParseNote(line.note);
                if (midi < 0)
                {
                    Debug.LogError($"[Lesson] {id}.json: can't read note \"{line.note}\" in bar {line.bar}.");
                    continue;
                }
                song.notes.Add(new SongNote
                {
                    midi = midi, bar = line.bar, beat = line.beat, beats = line.beats,
                    hand = !string.IsNullOrEmpty(line.hand) && char.ToLowerInvariant(line.hand.Trim()[0]) == 'l' ? PianoHand.Left : PianoHand.Right,
                    finger = line.finger, hint = string.IsNullOrWhiteSpace(line.hint) ? null : line.hint,
                });
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Lesson] {id}.json is not valid: {e.Message}");
        }
        return song;
    }

    // "C4" = 60, "D#5" = 75, "Bb3" = 58. -1 if it can't be read.
    public static int ParseNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        text = text.Trim();
        int i = 1;
        int pc = Array.IndexOf(Names, char.ToUpperInvariant(text[0]).ToString());
        if (pc < 0) return -1;
        if (i < text.Length && text[i] == '#') { pc++; i++; }
        else if (i < text.Length && text[i] == 'b') { pc--; i++; }
        if (!int.TryParse(text.Substring(i), out int octave)) return -1;
        return (octave + 1) * 12 + pc;
    }

    // Does this hand have anything to play in bars first..last?
    public static bool HasNotes(SongData song, int firstBar, int lastBar, PianoHand hand) =>
        song != null && song.notes.Any(n => n.hand == hand && n.bar >= firstBar && n.bar <= lastBar);

    static bool Plays(HandMode mode, PianoHand hand) =>
        mode == HandMode.Both || (mode == HandMode.Left) == (hand == PianoHand.Left);

    // The notes of bars first..last for the given hand(s), in playing order (notes struck together: lowest first).
    // time is seconds from the first of those notes at tempoPct.
    public static List<ExpectedNote> Expected(SongData song, int firstBar, int lastBar, int tempoPct, HandMode hands = HandMode.Right)
    {
        var barStart = BarStarts(song);
        var notes = song.notes
            .Where(n => Plays(hands, n.hand) && n.bar >= firstBar && n.bar <= lastBar)
            .OrderBy(n => barStart[n.bar] + n.beat).ThenBy(n => n.midi)
            .ToList();

        float secondsPerBeat = SecondsPerBeat(song, tempoPct);
        var list = new List<ExpectedNote>(notes.Count);
        float first = notes.Count > 0 ? barStart[notes[0].bar] + notes[0].beat - 1f : 0f;
        foreach (var n in notes)
        {
            float beat = barStart[n.bar] + n.beat - 1f;
            list.Add(new ExpectedNote
            {
                index = list.Count, midi = n.midi, bar = n.bar, beat = beat, duration = n.beats,
                finger = n.finger, hand = n.hand, hint = n.hint, time = (beat - first) * secondsPerBeat,
            });
        }
        return list;
    }

    public static float SecondsPerBeat(SongData song, int tempoPct) =>
        60f / Mathf.Max(1, song.tempoBpm) * 100f / Mathf.Clamp(tempoPct, 10, 200);

    // Beat (from the start of the song) where each bar starts. A bar is beatsPerBar long, or longer if its notes run past
    // that (bar 1 of Für Elise holds the pickup too).
    static Dictionary<int, float> BarStarts(SongData song)
    {
        var starts = new Dictionary<int, float>();
        float t = 0f;
        for (int bar = 1; bar <= song.BarCount; bar++)
        {
            starts[bar] = t;
            float length = song.beatsPerBar;
            foreach (var n in song.notes)
                if (n.bar == bar) length = Mathf.Max(length, n.beat - 1f + n.beats);
            t += length;
        }
        return starts;
    }
}
