using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Song openings for Free Play recognition, built from the song database: right hand (all notes if the song has
// none), top voice (highest note per onset), first N melody notes. Openings that are chord-heavy, too short or
// can never be distinctive (e.g. all repeated notes) are skipped, since they'd only cause false matches.
public static class SongTemplates
{
    const float ChordHeavyShare = 0.3f; // more than this share of opening onsets with 2+ notes = chord-heavy

    static readonly Dictionary<int, List<SongTemplate>> s_cache = new Dictionary<int, List<SongTemplate>>();

    public static List<SongTemplate> FromDatabase(int openingNotes = 16)
    {
        if (s_cache.TryGetValue(openingNotes, out var cached)) return cached;

        int minNotes = new FreePlaySettings().songMatchMinNotes;
        var list = new List<SongTemplate>();
        var skipped = new List<string>();
        foreach (var e in SongDatabase.All)
        {
            var song = SongDatabase.Get(e.id);
            if (song == null || song.notes.Count == 0) { skipped.Add(e.id + " (no notes)"); continue; }
            var notes = song.notes.Where(n => n.hand == PianoHand.Right).ToList();
            if (notes.Count == 0) notes = song.notes;
            int bpb = Mathf.Max(1, song.beatsPerBar);

            var onsets = notes
                .GroupBy(n => Mathf.RoundToInt(((n.bar - 1) * bpb + (n.beat - 1f)) * 1000f))
                .OrderBy(g => g.Key)
                .Take(openingNotes)
                .ToList();
            int multi = onsets.Count(g => g.Select(n => n.midi).Distinct().Count() >= 2);
            var opening = onsets.Select(g => g.Max(n => n.midi)).ToArray();

            if (opening.Length < minNotes) { skipped.Add(e.id + " (too short)"); continue; }
            if (multi > ChordHeavyShare * onsets.Count) { skipped.Add(e.id + " (chord-heavy)"); continue; }
            var intervals = new List<int>();
            for (int i = 1; i < opening.Length; i++) intervals.Add(opening[i] - opening[i - 1]);
            if (!FreePlayAnalyzer.IsDistinctive(intervals)) { skipped.Add(e.id + " (not distinctive)"); continue; }

            list.Add(new SongTemplate { id = e.id, name = string.IsNullOrEmpty(e.name) ? e.id : e.name, opening = opening });
        }

        Debug.Log($"[FreePlay] Matchable songs: {(list.Count > 0 ? string.Join(", ", list.Select(t => t.id)) : "none")}; " +
                  $"skipped: {(skipped.Count > 0 ? string.Join(", ", skipped) : "none")}");
        s_cache[openingNotes] = list;
        return list;
    }
}
