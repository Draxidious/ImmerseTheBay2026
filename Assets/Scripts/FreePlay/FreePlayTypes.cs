using System.Collections.Generic;

// Free Play data types. Plain C#, no Unity calls. Times are seconds on any monotonic clock, notes are MIDI numbers
// (middle C = 60), pitch class = midi % 12 (C = 0).

public class FreePlaySettings
{
    public double phraseGapSeconds = 2.0;    // silence (no key held) that ends a phrase
    public double chordWindowSeconds = 0.08; // onsets within this window of a group's first onset form one chord/group
    public int songMatchMinNotes = 6;        // consecutive matching notes (= minNotes-1 intervals)
    public int keyWindowNotes = 32;          // rolling key estimate over the last N notes (session-wide)
    public float keyMinCorrelation = 0.6f;   // KeyEstimate.IsClear needs correlation >= this ...
    public float keyMinMargin = 0.1f;        // ... and (best - second best) >= this. Tests: C-F-G-C 0.21, Am-Dm-E-Am 0.28, Am-F-C-G 0.105
}

public class FreePlayNote
{
    public int midi;
    public int velocity;
    public double onset;
    public double release = -1; // -1 = still held
}

public class ChordInfo
{
    public string name;     // "C", "Am", "G7", "Bdim", "Caug", "Dsus4", "Fmaj7", "Em7", "Bm7b5", "Bdim7", "C6", "Am6"; inversions "C/E"
    public int root;        // pitch class
    public string quality;  // "major","minor","diminished","augmented","sus2","sus4","dominant 7th","major 7th","minor 7th",
                            // "half-diminished 7th","diminished 7th","major 6th","minor 6th"
    public int bassMidi;    // lowest note
    public int inversion;   // 0 root position, 1 first, 2 second, 3 third
    public double onset;
    public int[] midis;     // sorted
}

public class KeyEstimate
{
    public int tonic;
    public bool major;
    public string name;       // "C major", "A minor" (sharps spelling, e.g. "F# minor")
    public float correlation; // best r
    public float margin;      // best - second best
    public bool IsClear;      // per the settings thresholds (and >= 3 distinct pitch classes)
}

// CV thresholds: < 0.15 steady, < 0.30 a bit uneven (FreePlayAnalyzer.SteadyMaxCv / ABitUnevenMaxCv), needs >= 5 onsets.
public enum Steadiness { NotEnoughNotes, Steady, ABitUneven, Uneven }

public class SongTemplate
{
    public string id;
    public string name;
    public int[] opening; // top-voice MIDI, first ~16 melody notes
}

public class SongMatch
{
    public string songId;
    public string songName;
    public int matchedNotes;
    public int transpose; // semitones vs the song
}

public class PhraseAnalysis
{
    public double start, end;
    public int noteCount;
    public int lowest, highest;        // midi
    public string hands;               // "right", "left", "both", "unknown" (register heuristic; "likely")
    public List<int> melody;           // top voice: highest note of each onset group, in order
    public List<ChordInfo> chords;     // in order; consecutive identical chords collapsed
    public List<string> romanNumerals; // same length as chords when the phrase key (or rolling key) IsClear, else null
    public KeyEstimate key;            // key of this phrase (duration-weighted); may be !IsClear
    public Steadiness steadiness;
    public float ioiCv;
    public SongMatch song;             // null if none
}

public class FreePlaySession
{
    public int phrases, notes;
    public int lowest = int.MaxValue, highest = int.MinValue;
    public List<string> songsRecognized = new List<string>(); // song ids, distinct, in order
    public List<string> chordsSeen = new List<string>();      // distinct names, in order
    public KeyEstimate rollingKey;                            // over the last keyWindowNotes notes of the session
}
