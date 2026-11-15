using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Tools ▸ Songs ▸ Generate Sheet Music: every song in Assets/Resources/SongDB (and SongDB/Local) → MusicXML (MusicXmlExporter)
/// → Verovio + resvg (Tools/SheetMusic/render_sheets.py, own Python venv) → Assets/Resources/Sheets/&lt;songId&gt;/page-NN.png
/// + layout.json (bar and note rectangles mapped back to the song database). Local songs go to Sheets/Local/ (gitignored).
///
/// One-time setup (no admin, nothing system-wide), from the project root:
///   uv venv Tools/SheetMusic/.venv --python 3.13
///   uv pip install --python Tools/SheetMusic/.venv/Scripts/python.exe verovio==6.4.1 resvg-py==0.5.0
/// </summary>
public static class SheetMusicMenu
{
    const string SongsDir = "Assets/Resources/SongDB";
    const string SheetsDir = "Assets/Resources/Sheets";
    const string ToolDir = "Tools/SheetMusic";
    static readonly Regex IdRx = new Regex(@"^n(\d+)(c\d+)?(p\d+)?$");

    [Serializable] class RawMeasure { public int order, page; public float x, y, w, h; }
    [Serializable] class RawNote { public string id; public int page, measureOrder, staff, pitch; public float qstamp, x, y, w, h; }
    [Serializable] class RawSystem { public int index, page, width, height; public string file; public float x, y, w, h; public List<int> measureOrders; }
    [Serializable] class RawSheet { public string song; public List<SheetPage> pages; public List<RawMeasure> measures; public List<RawNote> notes; public List<RawSystem> systems; }

    [MenuItem("Tools/Songs/Generate Sheet Music")]
    public static void GenerateAll() => Generate(null);

    /// <summary>Generate sheets for the given song ids (null = all). Returns a summary (also logged).</summary>
    public static string Generate(string[] onlyIds)
    {
        string root = Path.GetDirectoryName(Application.dataPath);
        string python = Path.Combine(root, ToolDir, ".venv", Application.platform == RuntimePlatform.WindowsEditor ? "Scripts/python.exe" : "bin/python");
        string script = Path.Combine(root, ToolDir, "render_sheets.py");
        if (!File.Exists(python))
        {
            string msg = "[Sheets] Python tools not set up. From the project root run:\n" +
                         "  uv venv Tools/SheetMusic/.venv --python 3.13\n" +
                         "  uv pip install --python Tools/SheetMusic/.venv/Scripts/python.exe verovio==6.4.1 resvg-py==0.5.0";
            Debug.LogError(msg);
            return msg;
        }

        // A fresh folder per run: the project lives in OneDrive, which can briefly lock files, so deleting the previous
        // run's folder may fail ("Access denied"). Old runs are cleaned up best-effort.
        string workRoot = Path.Combine(root, "Temp", "SheetMusic");
        if (Directory.Exists(workRoot))
            foreach (string old in Directory.GetDirectories(workRoot))
                try { Directory.Delete(old, true); } catch (Exception) { /* locked; try again next run */ }
        string work = Path.Combine(workRoot, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        string xmlDir = Path.Combine(work, "xml"), rawDir = Path.Combine(work, "raw");
        Directory.CreateDirectory(xmlDir);
        Directory.CreateDirectory(rawDir);

        // 1. Export every song to MusicXML.
        var exported = new Dictionary<string, (SongEntry entry, MusicXmlExporter.Result result, bool local)>();
        foreach (string guid in AssetDatabase.FindAssets("t:TextAsset", new[] { SongsDir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".json")) continue;
            string json = File.ReadAllText(path);
            var entry = JsonUtility.FromJson<SongEntry>(json);
            if (!SongDatabase.IsSongEntry(entry, json, out string why)) { Debug.LogWarning($"[Sheets] Skipping {path}: {why}"); continue; }
            if (onlyIds != null && !onlyIds.Contains(entry.id)) continue;
            bool local = path.Replace('\\', '/').Contains("/Local/");
            var result = MusicXmlExporter.Export(entry);
            File.WriteAllText(Path.Combine(xmlDir, (local ? "Local__" : "") + entry.id + ".musicxml"), result.xml, new UTF8Encoding(false));
            exported[entry.id] = (entry, result, local);
            DeleteOldPages(local ? $"{SheetsDir}/Local/{entry.id}" : $"{SheetsDir}/{entry.id}");
        }

        // 2. Engrave (Verovio) + rasterize (resvg).
        var psi = new ProcessStartInfo(python,
            $"-I \"{script}\" --xml-dir \"{xmlDir}\" --png-dir \"{Path.Combine(root, SheetsDir)}\" --raw-dir \"{rawDir}\" --width 2048")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        string stdout, stderr;
        using (var proc = Process.Start(psi))
        {
            stdout = proc.StandardOutput.ReadToEnd();
            stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(300000);
        }

        // 3. Map the rendered bars/notes back to the song database and write layout.json.
        var report = new StringBuilder("[Sheets] Generate Sheet Music\n").Append(stdout.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(stderr)) report.Append("stderr: ").Append(stderr.Trim()).Append('\n');
        bool problems = !string.IsNullOrWhiteSpace(stderr) || stdout.Contains("FAILED");
        foreach (var kv in exported)
        {
            string rawPath = Path.Combine(rawDir, kv.Key + ".json");
            if (!File.Exists(rawPath)) { report.Append($"  {kv.Key}: no layout (render failed)\n"); problems = true; continue; }
            var raw = JsonUtility.FromJson<RawSheet>(File.ReadAllText(rawPath));
            var (entry, result, local) = kv.Value;
            var layout = BuildLayout(entry.id, local, raw, result, out string check);
            if (check.Contains("MISSING") || check.Contains("MISMATCH")) problems = true;
            report.Append($"  {entry.id}: {check}\n");
            string dir = local ? $"{SheetsDir}/Local/{entry.id}" : $"{SheetsDir}/{entry.id}";
            Directory.CreateDirectory(dir);
            File.WriteAllText($"{dir}/layout.json", JsonUtility.ToJson(layout, true));
        }

        AssetDatabase.Refresh();
        ApplyImportSettings();
        SheetLibrary.ClearCache();
        string text = report.ToString();
        if (problems) Debug.LogWarning(text); else Debug.Log(text);
        return text;
    }

    static SheetLayout BuildLayout(string id, bool local, RawSheet raw, MusicXmlExporter.Result result, out string check)
    {
        var layout = new SheetLayout { songId = id, local = local, barCount = result.barCount, hasPickup = result.hasPickup, pages = raw.pages };
        foreach (var m in raw.measures.OrderBy(m => m.order))
        {
            bool pickup = result.hasPickup && m.order == 0;
            int bar = result.hasPickup ? Math.Max(1, m.order) : m.order + 1;
            layout.bars.Add(new SheetBar { bar = bar, pickup = pickup, page = m.page, x = m.x, y = m.y, w = m.w, h = m.h });
        }

        var heads = new HashSet<int>();
        int pitchMismatch = 0, unknown = 0;
        foreach (var n in raw.notes)
        {
            var match = IdRx.Match(n.id ?? "");
            if (!match.Success) { unknown++; continue; }
            int index = int.Parse(match.Groups[1].Value);
            if (index < 0 || index >= result.song.notes.Count) { unknown++; continue; }
            var src = result.song.notes[index];
            bool tied = n.id != $"n{index}";
            if (!tied) heads.Add(index);
            if (n.pitch >= 0 && n.pitch != src.midi) pitchMismatch++;
            layout.notes.Add(new SheetNote
            {
                noteIndex = index, bar = src.bar, beat = src.beat, midi = src.midi, hand = src.hand == PianoHand.Left ? "L" : "R",
                tied = tied, page = n.page, x = n.x, y = n.y, w = n.w, h = n.h,
            });
        }
        layout.notes = layout.notes.OrderBy(n => n.page).ThenBy(n => n.bar).ThenBy(n => n.beat).ThenBy(n => n.midi).ToList();

        // Lines of music (strips): bar edges and highlight box as fractions of the strip, in SongNoteDisplay's shape.
        // The pickup and bar 1 share one slot (the pickup counts as bar 1, as in the lesson code).
        foreach (var s in raw.systems ?? new List<RawSystem>())
        {
            var rects = raw.measures.Where(m => s.measureOrders.Contains(m.order))
                .Select(m => (bar: result.hasPickup ? Math.Max(1, m.order) : m.order + 1, m)).ToList();
            if (rects.Count == 0) continue;
            int first = rects.Min(r => r.bar), last = rects.Max(r => r.bar);
            var edges = new List<float>();
            for (int b = first; b <= last; b++)
            {
                var ofBar = rects.Where(r => r.bar == b).ToList();
                if (ofBar.Count > 0) edges.Add((ofBar.Min(r => r.m.x) - s.x) / s.w);
            }
            edges.Add((rects.Max(r => r.m.x + r.m.w) - s.x) / s.w);
            layout.systems.Add(new SheetSystemInfo
            {
                index = s.index, file = s.file, page = s.page, x = s.x, y = s.y, w = s.w, h = s.h, width = s.width, height = s.height,
                firstBar = first, lastBar = last, barEdges = edges.Select(v => Mathf.Clamp01(v)).ToArray(),
                top = Mathf.Clamp01((rects.Min(r => r.m.y) - s.y) / s.h),
                bottom = Mathf.Clamp01((rects.Max(r => r.m.y + r.m.h) - s.y) / s.h),
            });
        }

        int fullBars = layout.bars.Count(b => !b.pickup);
        var missing = Enumerable.Range(0, result.song.notes.Count).Where(i => !heads.Contains(i)).ToList();
        check = $"{raw.pages.Count} page(s), {fullBars}/{result.barCount} bars{(result.hasPickup ? " + pickup" : "")}, " +
                $"{heads.Count}/{result.song.notes.Count} notes placed" +
                (fullBars != result.barCount ? " — BAR COUNT MISMATCH" : "") +
                (missing.Count > 0 ? $" — MISSING notes {string.Join(",", missing.Take(10))}" : "") +
                (pitchMismatch > 0 ? $" — {pitchMismatch} PITCH MISMATCH(es)" : "") +
                (unknown > 0 ? $" — {unknown} unknown ids" : "");
        return layout;
    }

    /// <summary>
    /// Sheet PNGs: high-quality compression (ASTC 4x4 on Android, ~6 MB per 2048x2897 page instead of ~24 MB uncompressed),
    /// no mipmaps, usable as UI sprites, full resolution. Set per file here instead of an AssetPostprocessor, because adding a
    /// texture postprocessor makes Unity reimport every texture in the project. The settings are saved in each PNG's .meta
    /// file, so teammates get them without regenerating.
    /// </summary>
    static void ApplyImportSettings()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { SheetsDir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
            bool changed = ti.textureType != TextureImporterType.Sprite || ti.mipmapEnabled ||
                           ti.textureCompression != TextureImporterCompression.CompressedHQ || ti.maxTextureSize < 4096 ||
                           ti.npotScale != TextureImporterNPOTScale.None;
            if (!changed) continue;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.maxTextureSize = 4096;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.alphaIsTransparency = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.SaveAndReimport();
        }
    }

    static void DeleteOldPages(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "page-*.png").Concat(Directory.GetFiles(dir, "system-*.png")))
            AssetDatabase.DeleteAsset(f.Replace('\\', '/'));
    }
}


