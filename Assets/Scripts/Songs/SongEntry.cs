using System;
using System.Collections.Generic;

/// <summary>
/// One song as stored in the song database: a JSON file in Assets/Resources/SongDB/ (loaded with JsonUtility).
/// Converted to the AI layer's <see cref="SongData"/> by <see cref="SongNotation.ToSongData"/>.
///
/// Notes can be given two ways (both may be used in one file):
///  1. Bar strings, one per bar per hand (hand-typed songs), in the same notation the coach reads:
///       "E4 D4 C4:2"            one beat each unless ":beats" is given
///       "C4+E4+G4:2"            notes played together
///       "r:0.5"                 rest
///       "E4@3"                  finger (1 = thumb … 5 = little finger), per note: "C4@1+E4@3+G4@5"
///     Note names: C D E F G A B, optional # or b, octave (middle C = C4 = MIDI 60).
///     The beats in each bar should add up to beatsPerBar (the validator checks this).
///  2. Explicit notes (MIDI import): bar, beat (1 = first beat), length in beats, hand, finger.
/// </summary>
[Serializable]
public class SongEntry
{
    public string id;               // stable id, e.g. "fur_elise" (also SongData.id)
    public string name;
    public string composer;
    public string license = "Public domain";
    public string source;           // where the notes came from (typed by hand, file name + site, ...)
    public bool verified;           // checked note by note against the sheet music / by ear
    public bool teachable;          // the AI coach can run a guided lesson on it ("teach me X"); see CurriculumSongs.Teachable
    // Where the finger numbers (@n in the bar strings, "finger" in notes) come from and what still needs review,
    // e.g. "standard C five-finger position", "copied from <file>", "none — needs review". Empty in older files.
    public string fingering = "";
    public string arrangement = "as written"; // "as written", "simplified: melody + bass", ...

    public int difficulty = 1;      // 1 = first lesson … 5 = advanced
    public int order;               // curriculum order (lower first)

    public int tempoBpm = 100;      // beats per minute, in the beat unit below
    public int beatsPerBar = 4;
    public string beatUnit = "quarter"; // what one beat is: "quarter", "eighth", ...

    // Key signature, for sheet music: sharps (+) or flats (−), e.g. 0 = C major / A minor, −5 = D-flat major.
    // Also decides how black keys are spelled (sharps for ≥ 0, flats for < 0).
    public int keyFifths;
    public string keyMode = "major"; // "major" or "minor"

    public List<SongSection> sections = new List<SongSection>();

    // Hand-typed notes: one string per bar (index 0 = bar 1). Pickup = notes before bar 1 (anacrusis).
    public string pickupRH = "";
    public string pickupLH = "";
    public List<string> rh = new List<string>();
    public List<string> lh = new List<string>();

    // Imported notes (MIDI). Same meaning as SongData's SongNote.
    public List<SongNoteDef> notes = new List<SongNoteDef>();
}

[Serializable]
public class SongSection
{
    public string name;             // e.g. "A section (bars 1–8)"
    public int firstBar = 1;
    public int lastBar = 1;
}

[Serializable]
public class SongNoteDef
{
    public int midi;
    public int bar;
    public float beat = 1f;         // 1 = first beat of the bar
    public float beats = 1f;        // length in beats
    public string hand = "R";       // "R" or "L"
    public int finger;              // 0 = not specified
}
