using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// The song database: every JSON file under Assets/Resources/Songs/ (and its subfolders) is one <see cref="SongEntry"/>.
/// Ships inside the app (no server). Assets/Resources/Songs/Local/ is gitignored: songs that can't go in the public repo
/// (e.g. copyrighted pieces) go there and are still built into your own app.
///
///   var song = SongDatabase.Get("fur_elise");                       // SongData for the coach / lesson engine
///   var a    = SongDatabase.GetSection("fur_elise", "A section (bars 1–8)");
///   foreach (var e in SongDatabase.All) ...                          // metadata, in curriculum order
///   coachContext.song = SongDatabase.Get(id);                        // what the AI coach reads
/// </summary>
public static class SongDatabase
{
    public const string ResourcesFolder = "Songs";

    static List<SongEntry> s_entries;
    static Dictionary<string, SongEntry> s_byId;
    static readonly Dictionary<string, SongData> s_cache = new Dictionary<string, SongData>();

    /// <summary>All songs, in curriculum order (order, then difficulty, then name).</summary>
    public static IReadOnlyList<SongEntry> All { get { Load(); return s_entries; } }

    /// <summary>Song metadata (name, composer, sections, difficulty, ...) or null.</summary>
    public static SongEntry Entry(string id)
    {
        Load();
        return id != null && s_byId.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>The whole song (both hands) as SongData, or null if there's no song with that id.</summary>
    public static SongData Get(string id)
    {
        var e = Entry(id);
        if (e == null) { Debug.LogWarning($"[SongDatabase] No song with id \"{id}\"."); return null; }
        if (!s_cache.TryGetValue(e.id, out var song)) s_cache[e.id] = song = SongNotation.ToSongData(e);
        return song;
    }

    /// <summary>Bars firstBar..lastBar (sheet-music numbering kept), optionally one hand only.</summary>
    public static SongData Get(string id, int firstBar, int lastBar, PianoHand? onlyHand = null)
    {
        var e = Entry(id);
        return e == null ? null : SongNotation.ToSongData(e, firstBar, lastBar, onlyHand);
    }

    /// <summary>A named section from the song's "sections" list (e.g. "A section (bars 1–8)"), optionally one hand.</summary>
    public static SongData GetSection(string id, string sectionName, PianoHand? onlyHand = null)
    {
        var e = Entry(id);
        var s = e?.sections.FirstOrDefault(x => string.Equals(x.name, sectionName, StringComparison.OrdinalIgnoreCase));
        if (s == null) { Debug.LogWarning($"[SongDatabase] Song \"{id}\" has no section \"{sectionName}\"."); return null; }
        return SongNotation.ToSongData(e, s.firstBar, s.lastBar, onlyHand);
    }

    /// <summary>Songs up to a difficulty (1 = easiest), in curriculum order.</summary>
    public static IEnumerable<SongEntry> ForDifficulty(int maxDifficulty) => All.Where(e => e.difficulty <= maxDifficulty);

    /// <summary>Forget everything loaded (e.g. after editing a JSON file in the Editor).</summary>
    public static void Reload()
    {
        s_entries = null;
        s_byId = null;
        s_cache.Clear();
        SongFormatter.Invalidate();
    }

    static void Load()
    {
        if (s_entries != null) return;
        s_entries = new List<SongEntry>();
        s_byId = new Dictionary<string, SongEntry>();
        foreach (var asset in Resources.LoadAll<TextAsset>(ResourcesFolder))
        {
            SongEntry e;
            try { e = JsonUtility.FromJson<SongEntry>(asset.text); }
            catch (Exception ex) { Debug.LogError($"[SongDatabase] {asset.name}.json is not valid JSON: {ex.Message}"); continue; }
            if (e == null || string.IsNullOrWhiteSpace(e.id)) { Debug.LogError($"[SongDatabase] {asset.name}.json has no \"id\"."); continue; }
            if (s_byId.ContainsKey(e.id)) { Debug.LogError($"[SongDatabase] Duplicate song id \"{e.id}\" ({asset.name}.json); keeping the first."); continue; }
            s_byId[e.id] = e;
            s_entries.Add(e);
        }
        s_entries = s_entries.OrderBy(e => e.order).ThenBy(e => e.difficulty).ThenBy(e => e.name).ToList();
    }
}
