using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows the song from a PerformanceEvaluator as a piano roll (time left to right, one row per pitch) on a world-space
/// panel it builds itself, plus the next note, feedback on the last note and the running score.
/// No dark backing panel: the Aura display is additive, so black is invisible and only bright things show.
/// </summary>
public class SongNoteDisplay : MonoBehaviour
{
    public PerformanceEvaluator evaluator;

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

    private const float LabelWidth = 70f;
    private const float RollTop = 70f;

    private RectTransform _root;
    private readonly List<Image> _blocks = new List<Image>();
    private TMP_Text _title, _next, _feedback, _stats;
    private bool _built;

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
        // Pulse the note to play next so it stands out.
        if (!_built || evaluator == null || evaluator.IsFinished) return;
        float pulse = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 6f);
        _blocks[evaluator.CurrentIndex].color = Color.Lerp(upcomingColor, currentColor, pulse);
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

        _blocks[r.index].color = c;
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
    }

    // ------------------------------------------------------------------ Building the panel

    private void Build()
    {
        var canvasGo = new GameObject("Song Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
        _root = (RectTransform)canvasGo.transform;
        _root.sizeDelta = panelSize;
        _root.localScale = Vector3.one * metersPerUnit;

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

        _title = Text("Title", 0f, 0f, panelSize.x, 50f, 34f, TextAlignmentOptions.Center);

        foreach (var kv in rowOf)
        {
            float y = RollTop + kv.Value * rowHeight;
            var label = Text("Row " + SongNote.NameOf(kv.Key), 0f, y, LabelWidth - 8f, rowHeight, 20f, TextAlignmentOptions.Right);
            label.text = SongNote.NameOf(kv.Key);
            Box("Guide", LabelWidth, y + rowHeight * 0.5f - 1f, rollWidth, 2f, guideColor);
        }

        // Bar lines. The pickup makes bar 1 start late, so find where each bar begins from the notes.
        int lastBar = 0;
        foreach (var n in song.notes)
        {
            if (n.bar == lastBar) continue;
            lastBar = n.bar;
            if (n.bar == 1) continue;
            Box("Bar " + n.bar, LabelWidth + n.beat * unitsPerBeat - 2f, RollTop, 2f, rollHeight, guideColor);
        }

        foreach (var n in song.notes)
        {
            float x = LabelWidth + n.beat * unitsPerBeat;
            float w = Mathf.Max(n.duration * unitsPerBeat - 2f, 4f);
            _blocks.Add(Box(n.Name, x, RollTop + rowOf[n.midi] * rowHeight + 3f, w, rowHeight - 6f, upcomingColor));
        }

        float below = RollTop + rollHeight + 15f;
        _next = Text("Next", 0f, below, panelSize.x, 70f, 56f, TextAlignmentOptions.Center);
        _feedback = Text("Feedback", 0f, below + 75f, panelSize.x, 50f, 36f, TextAlignmentOptions.Center);
        _stats = Text("Stats", 0f, below + 130f, panelSize.x, 110f, 26f, TextAlignmentOptions.Top);
        _built = true;
    }

    // x, y are measured from the panel's top-left corner, y going down.
    private RectTransform Place(GameObject go, float x, float y, float w, float h)
    {
        var rt = (RectTransform)go.transform;
        rt.SetParent(_root, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    private Image Box(string name, float x, float y, float w, float h, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        Place(go, x, y, w, h);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private TMP_Text Text(string name, float x, float y, float w, float h, float size, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        Place(go, x, y, w, h);
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
