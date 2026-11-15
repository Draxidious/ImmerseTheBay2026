using System.Collections.Generic;
using System.Linq;

// Where in a song the player is, from the notes they press, for the free-play sheet. Lenient on purpose: free play is not
// a lesson, so a wrong or extra note is just ignored. Each moment of the song (an onset) is the set of pitches that start
// together, both hands. A pressed note moves the position when it is in one of the next few onsets; when the player has
// jumped elsewhere, the last few pressed notes are looked for anywhere in the song.
public class SheetFollower
{
    public const int LookAhead = 6;   // onsets ahead that a pressed note may land on
    public const int ResyncNotes = 4; // pressed notes in a row needed to jump anywhere in the song

    readonly List<(int bar, HashSet<int> pitches)> _onsets;
    readonly List<int> _recent = new List<int>();
    int _next; // index of the onset expected next

    public int Transpose; // semitones the player is above the song (SongMatch.transpose)

    // The bar of the last onset matched; 0 before any.
    public int CurrentBar { get; private set; }
    public int OnsetCount => _onsets.Count;

    public SheetFollower(SongData song)
    {
        _onsets = (song?.notes ?? new List<SongNote>())
            .GroupBy(n => (n.bar, n.beat))
            .OrderBy(g => g.Key.bar).ThenBy(g => g.Key.beat)
            .Select(g => (g.Key.bar, new HashSet<int>(g.Select(n => n.midi))))
            .ToList();
    }

    public void Reset()
    {
        _next = 0;
        CurrentBar = 0;
        _recent.Clear();
    }

    // True when the position (the bar) changed.
    public bool NoteOn(int midi)
    {
        if (_onsets.Count == 0) return false;
        int pitch = midi - Transpose;
        _recent.Add(pitch);
        if (_recent.Count > ResyncNotes) _recent.RemoveAt(0);

        int before = CurrentBar;
        int found = Find(pitch, _next, LookAhead);
        if (found < 0 && _recent.Count == ResyncNotes) found = FindRun();
        if (found < 0) return false; // a wrong or extra note: stay where we are
        _next = found + 1;
        CurrentBar = _onsets[found].bar;
        return CurrentBar != before;
    }

    int Find(int pitch, int from, int count)
    {
        for (int i = from; i < _onsets.Count && i < from + count; i++)
            if (_onsets[i].pitches.Contains(pitch)) return i;
        return -1;
    }

    // The last ResyncNotes pressed notes on consecutive onsets anywhere: the first such run at or after the current
    // position, else the first from the start (songs repeat phrases). Returns the onset of the newest note.
    int FindRun()
    {
        int runs = _onsets.Count - ResyncNotes + 1;
        for (int i = 0; i < runs; i++)
        {
            int start = (_next + i) % runs;
            bool all = true;
            for (int k = 0; k < ResyncNotes && all; k++) all = _onsets[start + k].pitches.Contains(_recent[k]);
            if (all) return start + ResyncNotes - 1;
        }
        return -1;
    }
}
