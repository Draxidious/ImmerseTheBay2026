using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools ▸ Songs ▸ Validate All                     — checks every song JSON (notation, bar lengths, key range, overlaps).
/// Tools ▸ Songs ▸ Print All (as the coach sees them) — SongFormatter text of every song, to compare with the sheet music.
/// Tools ▸ Songs ▸ Import MIDI Files (as written / simplified)
///     — every .mid in Assets/Music/ → Assets/Resources/Songs/&lt;id&gt;.json; Assets/Music/Local/ → Assets/Resources/Songs/Local/
///       (gitignored). Existing JSON files are never overwritten: delete one to re-import it.
/// </summary>
public static class SongMenu
{
    const string MusicDir = "Assets/Music";
    const string SongsDir = "Assets/Resources/Songs";

    [MenuItem("Tools/Songs/Validate All")]
    public static void ValidateAll()
    {
        SongDatabase.Reload();
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var e in SongDatabase.All) SongNotation.Validate(e, errors, warnings);

        var sb = new StringBuilder();
        sb.AppendLine($"[Songs] {SongDatabase.All.Count} songs, {errors.Count} errors, {warnings.Count} warnings.");
        foreach (var e in SongDatabase.All)
            sb.AppendLine($"  {e.order,2}. {e.id} — {e.name} ({e.composer}), difficulty {e.difficulty}, {SongDatabase.Get(e.id).notes.Count} notes, " +
                          $"{(e.verified ? "verified" : "NOT verified against the sheet music")}");
        foreach (var x in errors) sb.AppendLine("  ERROR: " + x);
        foreach (var x in warnings) sb.AppendLine("  warning: " + x);
        if (errors.Count > 0) Debug.LogError(sb.ToString());
        else if (warnings.Count > 0) Debug.LogWarning(sb.ToString());
        else Debug.Log(sb.ToString());
    }

    [MenuItem("Tools/Songs/Print All (as the coach sees them)")]
    public static void PrintAll()
    {
        SongDatabase.Reload();
        var sb = new StringBuilder("[Songs] As the AI coach reads them (SongFormatter):\n");
        foreach (var e in SongDatabase.All)
        {
            sb.AppendLine().AppendLine(SongFormatter.Format(SongDatabase.Get(e.id)));
            foreach (var s in e.sections) sb.AppendLine($"  section: {s.name} = bars {s.firstBar}–{s.lastBar}");
        }
        Debug.Log(sb.ToString());
    }

    [MenuItem("Tools/Songs/Import MIDI Files (as written)")]
    public static void ImportAsWritten() => ImportAll(simplify: false);

    [MenuItem("Tools/Songs/Import MIDI Files (simplified: melody + bass)")]
    public static void ImportSimplified() => ImportAll(simplify: true);

    static void ImportAll(bool simplify)
    {
        if (!Directory.Exists(MusicDir))
        {
            Directory.CreateDirectory(MusicDir);
            Debug.Log($"[Songs] Created {MusicDir}/. Put .mid files there (or in {MusicDir}/Local/ for songs that must stay out of git) and run this again.");
            AssetDatabase.Refresh();
            return;
        }

        int imported = 0, skipped = 0;
        foreach (string path in Directory.GetFiles(MusicDir, "*.mid", SearchOption.AllDirectories)
                                         .Concat(Directory.GetFiles(MusicDir, "*.midi", SearchOption.AllDirectories)))
        {
            string p = path.Replace('\\', '/');
            bool local = p.Contains("/Local/");
            string fileName = Path.GetFileNameWithoutExtension(p);
            string id = Regex.Replace(fileName.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
            string outDir = local ? SongsDir + "/Local" : SongsDir;
            string outPath = $"{outDir}/{id}.json";
            if (File.Exists(outPath)) { skipped++; Debug.Log($"[Songs] {outPath} exists, not overwriting (delete it to re-import)."); continue; }

            try
            {
                var o = new MidiSongImporter.Options { simplify = simplify };
                var e = MidiSongImporter.Import(p, id, fileName, o);
                e.source = $"Imported from {p}{(simplify ? " (simplified: top note RH, bottom note LH)" : "")}. Fill in composer/license/source.";
                e.composer = "";
                e.license = local ? "Not for the public repo (Local/, gitignored)" : "CHECK: must be public domain or allow redistribution";
                Directory.CreateDirectory(outDir);
                File.WriteAllText(outPath, JsonUtility.ToJson(e, true));
                imported++;
                Debug.Log($"[Songs] Imported {p} → {outPath}: {e.notes.Count} notes, {e.beatsPerBar}/{e.beatUnit}, {e.tempoBpm} bpm.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Songs] Couldn't import {p}: {ex.Message}");
            }
        }
        AssetDatabase.Refresh();
        SongDatabase.Reload();
        Debug.Log($"[Songs] MIDI import done: {imported} imported, {skipped} skipped. Run Tools ▸ Songs ▸ Validate All next.");
    }
}
