using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One line ("system") of a sheet music image and where its bars are.</summary>
[Serializable]
public class SheetSystem
{
    [Tooltip("One line of the score: black notes on a white page.")]
    public Texture2D image;
    [Tooltip("Song bar number of the first bar on this line.")]
    public int firstBar = 1;
    [Tooltip("Left edge of each bar as a fraction of the image width (0 = left, 1 = right), then the right edge of the last bar.")]
    public float[] barEdges = new float[0];
    [Tooltip("Top and bottom of the highlight box as a fraction of the image height, measured from the top.")]
    public float top = 0.13f;
    public float bottom = 0.88f;

    public int BarCount => Mathf.Max(barEdges.Length - 1, 0);
    public bool Contains(int bar) => bar >= firstBar && bar < firstBar + BarCount;
}

/// <summary>
/// Shows the song from a PerformanceEvaluator on a world-space panel it builds itself: the sheet music line being played
/// with a box around the current bar (played bars are tinted by how they went), plus the next note, feedback on the last
/// note and the running score. Without sheet images it falls back to a piano roll (time left to right, one row per pitch).
/// The sheet is a white page with black notes; the text around it is bright because the Aura display is additive.
/// </summary>
public class SongNoteDisplay : MonoBehaviour
{
    public PerformanceEvaluator evaluator;

    [Header("Sheet music (one entry per line of music, in order)")]
    public List<SheetSystem> sheet = new List<SheetSystem>();
    [Tooltip("Width / height of the sheet images.")]
    public float sheetAspect = 3.52f;
    [Tooltip("Tint of the page itself. White shows the image as it is.")]
    public Color pageColor = Color.white;
    [Tooltip("Box around the bar being played. Needs to stand out against the white page.")]
    public Color frameColor = new Color(0.05f, 0.45f, 1f, 1f);

    [Header("Panel (canvas units; 1 unit = 1 mm at the default scale)")]
    public Vector2 panelSize = new Vector2(900f, 600f);
    public float metersPerUnit = 0.001f;
    public float rowHeight = 26f;

    [Header("Colors (bright: black is invisible on the glasses)")]
    public Color upcomingColor = new Color(0.20f, 0.55f, 0.75f, 1f);
    public Color currentColor = Color.white;
    public Color perfectColor = new Color(0.25f, 1f, 0.35f, 1f);
    public Color goodColor = new Color(0.75f, 1f, 0.25f, 1f);
    public Color offTempoColor = new Color(1f, 0.65f, 0.10f, 1f);
    public Color wrongColor = new Color(1f, 0.25f, 0.25f, 1f);
    public Color textColor = new Color(0.85f, 0.95f, 1f, 1f);
    public Color guideColor = new Color(0.4f, 0.7f, 0.9f, 0.18f);
    [Range(0f, 1f)] public float barFillAlpha = 0.4f;

    private const float LabelWidth = 70f;
    private const float AreaTop = 70f;
    private const float FrameThickness = 5f;

    private RectTransform _root;
    private TMP_Text _title, _next, _feedback, _stats;
    private bool _built;

    // Piano roll
    private readonly List<Image> _blocks = new List<Image>();

    // Sheet music
    private RawImage _sheetImage;
    private RectTransform _sheetRect;
    private readonly List<Image> _barFills = new List<Image>(); // one per bar of the line being shown
    private readonly Image[] _frame = new Image[4];
    private int _shownSystem = -1;

    private bool UseSheet => sheet != null && sheet.Count > 0 && sheet[0].image != null;

    private void OnEnable()
    {
        if (evaluator == null) evaluator = GetComponent<PerformanceEvaluator>();
        if (evaluator == null) evaluator = FindAnyObjectByType<PerformanceEvaluator>();
        if (evaluator == null) return;
        evaluator.Restarted += OnRestarted;
        evaluator.NoteJudged += OnNoteJudged;
        evaluator.WrongNotePlayed += OnWrongNote;
        evaluator.SongFinished += OnFinished;
    }

    private void OnDisable()
    {
        if (evaluator == null) return;
        evaluator.Restarted -= OnRestarted;
        evaluator.NoteJudged -= OnNoteJudged;
        evaluator.WrongNotePlayed -= OnWrongNote;
        evaluator.SongFinished -= OnFinished;
    }

    private void Update()
    {
        // Pulse what to play next so it stands out.
        if (!_built || evaluator == null || evaluator.IsFinished) return;
        float pulse = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 6f);
        if (UseSheet)
        {
            Color c = frameColor;
            c.a = pulse;
            foreach (var side in _frame) side.color = c;
        }
        else
        {
            _blocks[evaluator.CurrentIndex].color = Color.Lerp(upcomingColor, currentColor, pulse);
        }
    }

    // ------------------------------------------------------------------ Evaluator events

    private void OnRestarted()
    {
        if (!_built) Build();
        foreach (var b in _blocks) b.color = upcomingColor;
        _feedback.text = "Play the first note to begin";
        _feedback.color = textColor;
        Refresh();
    }

    private void OnNoteJudged(NoteResult r)
    {
        if (!_built) return;
        Color c;
        string word;
        switch (r.timing)
        {
            case TimingJudgement.Perfect: c = perfectColor; word = "Perfect"; break;
            case TimingJudgement.Good: c = goodColor; word = r.ratio < 1f ? "Good (a bit early)" : "Good (a bit late)"; break;
            case TimingJudgement.Early: c = offTempoColor; word = "Too early"; break;
            case TimingJudgement.Late: c = offTempoColor; word = "Too late"; break;
            default: c = perfectColor; word = "Go!"; break;
        }
        if (r.timing != TimingJudgement.None) word += $"  {r.timingErrorMs:+0;-0} ms";
        if (!r.FirstTry) { c = wrongColor; word = $"{r.note.Name} found after {r.wrongAttempts} wrong"; }

        if (!UseSheet) _blocks[r.index].color = c;
        _feedback.text = word;
        _feedback.color = c;
        Refresh();
    }

    private void OnWrongNote(int expected, int played)
    {
        if (!_built) return;
        _feedback.text = $"Wrong: {SongNote.NameOf(played)}   (need {SongNote.NameOf(expected)})";
        _feedback.color = wrongColor;
        Refresh();
    }

    private void OnFinished(PerformanceMetrics m)
    {
        if (!_built) return;
        _next.text = $"Grade {evaluator.Grade}";
        string bars = "";
        if (m.rushedBars.Count > 0) bars += "Rushed bars: " + string.Join(", ", m.rushedBars) + "   ";
        if (m.draggedBars.Count > 0) bars += "Dragged bars: " + string.Join(", ", m.draggedBars);
        _feedback.text = bars == "" ? "Steady tempo all the way" : bars;
        _feedback.color = bars == "" ? perfectColor : offTempoColor;
        Refresh();
        _stats.text += $"\nPlay {evaluator.Song.notes[0].Name} to try again";
    }

    private void Refresh()
    {
        var song = evaluator.Song;
        _title.text = $"{song.name}   target {song.bpm:0} bpm ({song.beatUnit})";
        if (!evaluator.IsFinished)
            _next.text = $"Next: {evaluator.CurrentNote.Name}   <size=55%>bar {evaluator.CurrentNote.bar}, note {evaluator.CurrentIndex + 1}/{song.notes.Count}</size>";

        string tempo = evaluator.PlayedBpm > 0f ? $"{evaluator.PlayedBpm:0} bpm" : "-";
        _stats.text =
            $"Notes {evaluator.AccuracyPercent:0}%   Timing {evaluator.TimingPercent:0}%   Overall {evaluator.OverallPercent:0}%\n" +
            $"Your tempo {tempo}   Wrong presses {evaluator.WrongPressCount}\n" +
            $"Perfect {evaluator.CountTiming(TimingJudgement.Perfect)} / Good {evaluator.CountTiming(TimingJudgement.Good)} / " +
            $"Off {evaluator.CountTiming(TimingJudgement.Early) + evaluator.CountTiming(TimingJudgement.Late)}";

        if (UseSheet) RefreshSheet();
    }

    // ------------------------------------------------------------------ Sheet music

    private void RefreshSheet()
    {
        // Stay on the last line once the song is finished, so its result is visible.
        int bar = evaluator.IsFinished ? evaluator.Song.notes[evaluator.Song.notes.Count - 1].bar : evaluator.CurrentNote.bar;
        int index = sheet.FindIndex(s => s.Contains(bar));
        if (index < 0) index = Mathf.Clamp(_shownSystem, 0, sheet.Count - 1);
        if (index != _shownSystem) ShowSystem(index);

        var system = sheet[index];
        for (int i = 0; i < _barFills.Count; i++)
        {
            int b = system.firstBar + i;
            bool current = !evaluator.IsFinished && b == bar;
            Color c = current ? frameColor : BarColor(b);
            c.a = current ? barFillAlpha * 0.4f : c.a * barFillAlpha;
            _barFills[i].color = c;
            if (current) MoveFrame((RectTransform)_barFills[i].transform);
        }
        foreach (var side in _frame) side.gameObject.SetActive(!evaluator.IsFinished);
    }

    /// <summary>Colour for a bar that has been played; transparent if no note of it has been played yet.</summary>
    private Color BarColor(int bar)
    {
        int notes = 0, wrong = 0, off = 0, perfect = 0, timed = 0;
        foreach (var r in evaluator.Results)
        {
            if (r.note.bar != bar) continue;
            notes++;
            if (!r.FirstTry) wrong++;
            if (r.timing == TimingJudgement.None) continue;
            timed++;
            if (r.timing == TimingJudgement.Perfect) perfect++;
            else if (r.timing != TimingJudgement.Good) off++;
        }
        if (notes == 0) return Color.clear;
        if (wrong > 0) return wrongColor;
        if (off > 0) return offTempoColor;
        return perfect * 2 >= timed ? perfectColor : goodColor;
    }

    private void ShowSystem(int index)
    {
        _shownSystem = index;
        var system = sheet[index];
        _sheetImage.texture = system.image;

        foreach (var f in _barFills) Destroy(f.gameObject);
        _barFills.Clear();

        float w = _sheetRect.sizeDelta.x, h = _sheetRect.sizeDelta.y;
        for (int i = 0; i < system.BarCount; i++)
        {
            float x0 = system.barEdges[i] * w, x1 = system.barEdges[i + 1] * w;
            var fill = Box("Bar " + (system.firstBar + i), _sheetRect, x0, system.top * h, x1 - x0, (system.bottom - system.top) * h, Color.clear);
            fill.transform.SetAsFirstSibling(); // behind the frame
            _barFills.Add(fill);
        }
    }

    private void MoveFrame(RectTransform bar)
    {
        float x = bar.anchoredPosition.x, y = -bar.anchoredPosition.y, w = bar.sizeDelta.x, h = bar.sizeDelta.y, t = FrameThickness;
        SetRect(_frame[0], x, y, w, t);         // top
        SetRect(_frame[1], x, y + h - t, w, t); // bottom
        SetRect(_frame[2], x, y, t, h);         // left
        SetRect(_frame[3], x + w - t, y, t, h); // right
    }

    // ------------------------------------------------------------------ Building the panel

    private void Build()
    {
        var canvasGo = new GameObject("Song Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        canvasGo.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
        _root = (RectTransform)canvasGo.transform;
        _root.sizeDelta = panelSize;
        _root.localScale = Vector3.one * metersPerUnit;

        _title = Text("Title", 0f, 0f, panelSize.x, 50f, 34f, TextAlignmentOptions.Center);
        float areaHeight = UseSheet ? BuildSheet() : BuildPianoRoll();

        float below = AreaTop + areaHeight + 15f;
        _next = Text("Next", 0f, below, panelSize.x, 70f, 56f, TextAlignmentOptions.Center);
        _feedback = Text("Feedback", 0f, below + 75f, panelSize.x, 50f, 36f, TextAlignmentOptions.Center);
        _stats = Text("Stats", 0f, below + 130f, panelSize.x, 110f, 26f, TextAlignmentOptions.Top);
        _built = true;
    }

    private float BuildSheet()
    {
        float height = panelSize.x / Mathf.Max(sheetAspect, 0.01f);
        var go = new GameObject("Sheet", typeof(RectTransform), typeof(RawImage));
        _sheetRect = Place(go, _root, 0f, AreaTop, panelSize.x, height);
        _sheetImage = go.GetComponent<RawImage>();
        _sheetImage.color = pageColor;
        _sheetImage.raycastTarget = false;
        for (int i = 0; i < 4; i++) _frame[i] = Box("Frame", _sheetRect, 0f, 0f, 1f, 1f, frameColor);
        _shownSystem = -1;
        return height;
    }

    private float BuildPianoRoll()
    {
        var song = evaluator.Song;

        // One row per pitch the song uses, highest at the top.
        var pitches = new SortedSet<int>();
        foreach (var n in song.notes) pitches.Add(n.midi);
        var rowOf = new Dictionary<int, int>();
        int rows = pitches.Count, row = rows - 1;
        foreach (int p in pitches) rowOf[p] = row--;

        float rollWidth = panelSize.x - LabelWidth - 10f;
        float unitsPerBeat = rollWidth / Mathf.Max(song.TotalBeats, 1f);
        float rollHeight = rows * rowHeight;

        foreach (var kv in rowOf)
        {
            float y = AreaTop + kv.Value * rowHeight;
            var label = Text("Row " + SongNote.NameOf(kv.Key), 0f, y, LabelWidth - 8f, rowHeight, 20f, TextAlignmentOptions.Right);
            label.text = SongNote.NameOf(kv.Key);
            Box("Guide", _root, LabelWidth, y + rowHeight * 0.5f - 1f, rollWidth, 2f, guideColor);
        }

        // Bar lines, placed where the first note of each new bar starts.
        int lastBar = 0;
        foreach (var n in song.notes)
        {
            if (n.bar == lastBar) continue;
            bool first = lastBar == 0;
            lastBar = n.bar;
            if (!first) Box("Bar " + n.bar, _root, LabelWidth + n.beat * unitsPerBeat - 2f, AreaTop, 2f, rollHeight, guideColor);
        }

        foreach (var n in song.notes)
        {
            float x = LabelWidth + n.beat * unitsPerBeat;
            float w = Mathf.Max(n.duration * unitsPerBeat - 2f, 4f);
            _blocks.Add(Box(n.Name, _root, x, AreaTop + rowOf[n.midi] * rowHeight + 3f, w, rowHeight - 6f, upcomingColor));
        }
        return rollHeight;
    }

    // x, y are measured from the parent's top-left corner, y going down.
    private static RectTransform Place(GameObject go, RectTransform parent, float x, float y, float w, float h)
    {
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    private static void SetRect(Image img, float x, float y, float w, float h)
    {
        var rt = (RectTransform)img.transform;
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static Image Box(string name, RectTransform parent, float x, float y, float w, float h, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        Place(go, parent, x, y, w, h);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private TMP_Text Text(string name, float x, float y, float w, float h, float size, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        Place(go, _root, x, y, w, h);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.fontSize = size;
        t.alignment = align;
        t.color = textColor;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
        return t;
    }
}
