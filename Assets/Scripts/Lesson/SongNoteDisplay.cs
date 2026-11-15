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
    [Tooltip("The guided lesson. When there is one in the scene the panel follows its runs instead of the evaluator: " +
             "the measures being played, which hand, the tempo step, and each measure's rating after a run.")]
    public CurriculumEngine engine;

    [Header("Hands (guided lesson)")]
    public Color rightHandColor = new Color(0.3f, 0.85f, 1f, 1f);
    public Color leftHandColor = new Color(1f, 0.72f, 0.10f, 1f);

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
    public Color chipYellowColor = new Color(1f, 0.88f, 0.15f, 1f);
    public Color textColor = new Color(0.85f, 0.95f, 1f, 1f);
    public Color guideColor = new Color(0.4f, 0.7f, 0.9f, 0.18f);
    [Range(0f, 1f)] public float barFillAlpha = 0.4f;

    private const float LabelWidth = 70f;
    private const float AreaTop = 70f;
    private const float FrameThickness = 5f;

    private RectTransform _root;
    private TMP_Text _title, _next, _feedback, _stats;
    private bool _built;
    private FingeringGuide _fingeringGuide;

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
        if (engine == null) engine = FindAnyObjectByType<CurriculumEngine>();
        if (engine != null)
        {
            _fingeringGuide = FindAnyObjectByType<FingeringGuide>();
            engine.OnRunStarted += OnRunStarted;
            engine.OnNextNote += OnNoteDue;
            engine.OnNotePlayed += OnNotePlayed;
            engine.OnRunRated += OnRunRated;
            engine.OnStateChanged += OnLessonState;
            engine.OnShowFingering += OnShowFingering;
            engine.OnDemoNoteDown += OnDemoNoteDown;
            return;
        }
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
        if (engine != null)
        {
            engine.OnRunStarted -= OnRunStarted;
            engine.OnNextNote -= OnNoteDue;
            engine.OnNotePlayed -= OnNotePlayed;
            engine.OnRunRated -= OnRunRated;
            engine.OnStateChanged -= OnLessonState;
            engine.OnShowFingering -= OnShowFingering;
            engine.OnDemoNoteDown -= OnDemoNoteDown;
            return;
        }
        if (evaluator == null) return;
        evaluator.Restarted -= OnRestarted;
        evaluator.NoteJudged -= OnNoteJudged;
        evaluator.WrongNotePlayed -= OnWrongNote;
        evaluator.SongFinished -= OnFinished;
    }

    private void Update()
    {
        // Pulse what to play next so it stands out.
        if (!_built) return;
        float pulse = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 6f);
        if (engine != null)
        {
            // The lesson went on while the verdict was held; show what is next once it has been on screen long enough.
            if (_statePending && _resultShown && !engine.ResultHeld) ShowUpcoming();
            UpdateCue();
            if (!UseSheet || !_runActive) return;
            Color c = frameColor;
            c.a = pulse;
            foreach (var side in _frame) side.color = c;
            return;
        }
        if (evaluator == null || evaluator.IsFinished) return;
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
        _feedback.text = $"Wrong: {LessonNote.NameOf(played)}   (need {LessonNote.NameOf(expected)})";
        _feedback.color = wrongColor;
        Refresh();
    }

    private void OnFinished(RunStats m)
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

        if (UseSheet)
        {
            // Stay on the last line once the song is finished, so its result is visible.
            int bar = evaluator.IsFinished ? evaluator.Song.notes[evaluator.Song.notes.Count - 1].bar : evaluator.CurrentNote.bar;
            RefreshSheet(bar, !evaluator.IsFinished, BarColor);
        }
    }

    // ------------------------------------------------------------------ Guided lesson (CurriculumEngine)

    private bool _runActive;
    private int _lessonBar = 1;                 // the bar the frame is on
    private readonly Dictionary<int, string> _ratings = new Dictionary<int, string>(); // bars of the last rated run

    private string HandName(HandMode hand)
    {
        string hex = ColorUtility.ToHtmlStringRGB(hand == HandMode.Left ? leftHandColor : hand == HandMode.Right ? rightHandColor : currentColor);
        string name = hand == HandMode.Left ? "Left hand" : hand == HandMode.Right ? "Right hand" : "Both hands";
        return $"<color=#{hex}>{name}</color>";
    }

    private static string Bars(BarRange r) => r.First == r.Last ? $"measure {r.First}" : $"measures {r.First}-{r.Last}";

    // "Measure 3 of 8", "Play-through, measures 1-4", "Repair measure 5 (lead-in from 3)".
    private string LessonHeader()
    {
        switch (engine.State)
        {
            case CurriculumState.WarmupDemo:
            case CurriculumState.WarmupPlay: return "Warm-up: C major scale";
            case CurriculumState.SongLearn: return $"Measure {engine.Measure} of {engine.MeasureCount}";
            case CurriculumState.SongPlayThrough: return $"Play-through, {Bars(engine.CurrentRange)}";
            case CurriculumState.SongRepair:
                return $"Repair {Bars(engine.CurrentBlock)}" + (engine.LeadInFirstBar > 0 ? $" (lead-in from {engine.LeadInFirstBar})" : "");
            case CurriculumState.SongComplete: return "Piece complete";
            default: return engine.Song != null ? engine.Song.name : "";
        }
    }

    private bool _resultShown;     // the last run's verdict is on the panel and must stay until it has been read
    private bool _statePending;    // the lesson moved on while the verdict was held: show what is next once it is released
    private bool _statsFingering;

    private void OnLessonState(CurriculumState state)
    {
        if (!_built) Build();
        if (state == CurriculumState.SongComplete)
        {
            _title.text = LessonHeader();
            _runActive = false;
            _next.text = "Well done!";
            ShowTempo(null, 0);
            if (UseSheet) RefreshSheet(_lessonBar, false, RatingColor);
            return;
        }
        // The engine sets up the next measure right after rating a run: keep the verdict (and its bar) up until it is released.
        if (_resultShown) _statePending = true;
        else ShowUpcoming();
    }

    private void OnDemoNoteDown(ExpectedNote note)
    {
        if (!_built) return;
        if (_resultShown) ShowUpcoming(); // a demo only starts once the result has been held long enough
        _demoHandoff = true;
        ShowCue("<size=60%>Watch the demo</size>");
    }

    // What is about to be played, before its run starts: header, and the sheet line with the box on the first bar.
    // This is what makes the sheet appear when the lesson reaches the song, not on the first run.
    private void ShowUpcoming()
    {
        _resultShown = false;
        _statePending = false;
        SetChips(false);
        _feedback.text = "";
        if (!_statsFingering) _stats.text = "";
        _ratings.Clear();

        var state = engine.State;
        _title.text = LessonHeader();
        bool warmup = state == CurriculumState.WarmupDemo || state == CurriculumState.WarmupPlay;
        bool song = state == CurriculumState.SongIntro || state == CurriculumState.SongLearn ||
                    state == CurriculumState.SongPlayThrough || state == CurriculumState.SongRepair;
        ShowTempo(state == CurriculumState.SongIntro || !(song || warmup) ? null : warmup ? engine.Warmup : engine.Song, engine.TempoPct);
        if (!song) return;
        if (state != CurriculumState.SongIntro)
            _title.text += $"  ·  {HandName(engine.Hand)}";

        if (!UseSheet) return;
        int bar = state == CurriculumState.SongRepair ? (engine.LeadInFirstBar > 0 ? engine.LeadInFirstBar : engine.CurrentBlock.First)
                : state == CurriculumState.SongIntro ? 1 : engine.CurrentRange.First;
        _lessonBar = Mathf.Max(1, bar);
        RefreshSheet(_lessonBar, state != CurriculumState.SongIntro, RatingColor);
    }

    private string _cue;      // what the big line shows now
    private string _runLine;  // what it shows once the student is playing: hand and speed
    private bool _demoHandoff;

    private void ShowCue(string text)
    {
        if (text == _cue) return;
        _cue = text;
        _next.text = text;
    }

    // The big line counts the student in with the metronome: 4, 3, 2, 1, then "Play!" on the beat the music starts.
    private void UpdateCue()
    {
        if (engine.IsDemoPlaying) return;
        if (engine.Pause == LessonPause.WaitingForStudent)
        {
            ShowCue("<size=60%>Press any key when you're ready</size>");
            return;
        }
        var run = engine.CurrentRun;
        if (!_runActive || run == null)
        {
            if (_demoHandoff) ShowCue("<size=60%>Your turn</size>");
            return;
        }

        double now = Time.unscaledTimeAsDouble;
        if (now < run.goTime)
        {
            int clicked = 0;
            foreach (double t in run.countIn) if (t <= now) clicked++;
            ShowCue(clicked == 0 ? ReadyCue() : (run.countIn.Count - clicked + 1).ToString());
            if (clicked > 0) _demoHandoff = false;
        }
        else if (now < run.goTime + 1.2)
        {
            _demoHandoff = false;
            ShowCue($"<color=#{ColorUtility.ToHtmlStringRGB(perfectColor)}>Play!</color>");
        }
        else ShowCue(_runLine);
    }

    private string ReadyCue() => _demoHandoff ? "<size=60%>Your turn</size>" : "<size=60%>Get ready</size>";

    private void OnRunStarted(RunInfo run)
    {
        if (!_built) Build();
        _runActive = true;
        _resultShown = false;
        _statePending = false;
        _statsFingering = false;
        _ratings.Clear();
        _lessonBar = run.notes.Count > 0 ? run.notes[0].bar : run.bars.First;
        _title.text = $"{LessonHeader()}  ·  {HandName(run.hand)}";
        _runLine = HandName(run.hand);
        ShowTempo(run.song, run.tempoPct);
        _cue = null;
        ShowCue(ReadyCue());
        _feedback.text = "Start playing when it says Play";
        _feedback.color = textColor;
        SetChips(false);
        _stats.text = "";
        bool sheetSong = engine.State != CurriculumState.WarmupPlay && engine.State != CurriculumState.WarmupDemo;
        if (UseSheet) RefreshSheet(_lessonBar, sheetSong, RatingColor);
    }

    private void OnNoteDue(ExpectedNote note)
    {
        if (!_built) return;
        if (note == null) // the run is over
        {
            _runActive = false;
            if (UseSheet) RefreshSheet(_lessonBar, false, RatingColor);
            return;
        }
        if (note.bar == _lessonBar || !UseSheet) return;
        _lessonBar = note.bar;
        if (engine.State != CurriculumState.WarmupPlay) RefreshSheet(_lessonBar, true, RatingColor);
    }

    private void OnNotePlayed(ExpectedNote note, int key, bool correct)
    {
        if (!_built) return;
        _feedback.text = correct ? LessonNote.NameOf(key) : $"Wrong: {LessonNote.NameOf(key)}   (need {LessonNote.NameOf(note.midi)})";
        _feedback.color = correct ? perfectColor : wrongColor;
    }

    private void OnShowFingering(int bar, int[] fingers)
    {
        if (!_built || fingers == null || fingers.Length == 0) return;
        if (_fingeringGuide && _fingeringGuide.TryReplayFingering(engine, bar))
        {
            if (_stats.text.StartsWith("Fingering,", StringComparison.Ordinal)) _stats.text = "";
            return;
        }
        _stats.text = $"Fingering, measure {bar}: {string.Join(" ", fingers)}   (1 = thumb)";
        _statsFingering = true;
    }

    // The run has been judged: tint every judged measure by its rating and say how the run went.
    private void OnRunRated(BarRange judged)
    {
        if (!_built) return;
        _ratings.Clear();
        foreach (var kv in engine.LastRatings) _ratings[kv.Key] = kv.Value;

        _feedback.text = engine.LastPassed ? "Passed" : "Try again";
        _feedback.color = engine.LastPassed ? perfectColor : offTempoColor;
        if (engine.LastRunSilent)
        {
            _feedback.text = "I didn't hear any notes. Start when it says Play.";
            _feedback.color = offTempoColor;
            SetChips(false);
        }
        else ShowRunChips();

        _resultShown = true;
        _statePending = false;
        _statsFingering = false;
        var lines = new List<string>();
        foreach (var kv in engine.LastRatings)
        {
            if (kv.Value == "clean") continue;
            bool right = engine.LastRightRatings.TryGetValue(kv.Key, out string r) && r != "clean";
            bool left = engine.LastLeftRatings.TryGetValue(kv.Key, out string l) && l != "clean";
            string hands = right && left ? "both hands" : left ? "left hand" : "right hand";
            lines.Add($"Measure {kv.Key}: {kv.Value}, {hands}");
            if (lines.Count >= 3) break;
        }
        _stats.text = engine.LastRunSilent ? "" : string.Join("\n", lines);
        if (UseSheet && engine.State != CurriculumState.WarmupPlay) RefreshSheet(_lessonBar, false, RatingColor);
    }

    // The big line is a flexbox row: the cue / hand text takes whatever width is left (and shrinks its font to fit), and the
    // speed badge - a white metronome icon (Resources/UI/metronome.png) and "40 BPM" - sits at the right with its own width.
    // So a long cue like "Press any key when you're ready" can never run into the badge.
    private GameObject _bpmBadge;
    private TMP_Text _bpmText;

    private void ShowTempo(SongData song, int tempoPct)
    {
        if (_bpmBadge == null) return;
        bool show = song != null;
        _bpmBadge.SetActive(show);
        if (show) _bpmText.text = $"{Mathf.RoundToInt(song.tempoBpm * tempoPct / 100f)} BPM";
    }

    private void BuildNextRow(float y)
    {
        var rowGo = new GameObject("Next Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        Place(rowGo, _root, 0f, y, panelSize.x, 70f);
        var row = rowGo.GetComponent<HorizontalLayoutGroup>();
        row.childAlignment = TextAnchor.MiddleCenter;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = false;
        row.childForceExpandHeight = true;
        row.spacing = 16f;
        row.padding = new RectOffset(10, 10, 0, 0);

        // The cue takes all the room the badge leaves, and shrinks its text rather than spilling over.
        _next.transform.SetParent(rowGo.transform, false);
        _next.enableAutoSizing = true;
        _next.fontSizeMax = 56f;
        _next.fontSizeMin = 20f;
        var cue = _next.gameObject.AddComponent<LayoutElement>();
        cue.flexibleWidth = 1f;
        cue.preferredWidth = 0f;

        _bpmBadge = new GameObject("Tempo Badge", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        _bpmBadge.transform.SetParent(rowGo.transform, false);
        var badge = _bpmBadge.GetComponent<HorizontalLayoutGroup>();
        badge.childAlignment = TextAnchor.MiddleCenter;
        badge.childControlWidth = badge.childControlHeight = true;
        badge.childForceExpandWidth = badge.childForceExpandHeight = false;
        badge.spacing = 8f;

        var iconGo = new GameObject("Metronome Icon", typeof(RectTransform), typeof(RawImage), typeof(LayoutElement));
        iconGo.transform.SetParent(_bpmBadge.transform, false);
        var icon = iconGo.GetComponent<RawImage>();
        icon.texture = Resources.Load<Texture2D>("UI/metronome");
        icon.color = Color.white; // the texture is white on transparent: black would be invisible on the glasses
        icon.raycastTarget = false;
        var iconSize = iconGo.GetComponent<LayoutElement>();
        iconSize.preferredWidth = iconSize.preferredHeight = 42f;

        _bpmText = Text("Tempo", 0f, 0f, 150f, 70f, 40f, TextAlignmentOptions.MidlineLeft);
        _bpmText.transform.SetParent(_bpmBadge.transform, false);
        _bpmText.color = Color.white;
        _bpmText.fontStyle = FontStyles.Bold;
        var label = _bpmText.gameObject.AddComponent<LayoutElement>();
        label.preferredWidth = 150f; // fixed, so the cue does not jiggle as the number changes
        ShowTempo(null, 0);
    }

    // Two chips beside the verdict: how steady the speed was, and how accurate the notes were.
    // Green = good, yellow = passable but off, red = what failed the run.
    private Image _tempoChip, _accuracyChip;
    private TMP_Text _tempoChipText, _accuracyChipText;

    private void SetChips(bool visible)
    {
        if (_tempoChip == null) return;
        _tempoChip.gameObject.SetActive(visible);
        _accuracyChip.gameObject.SetActive(visible);
        // With chips the verdict sits on the left half of its row; without them it is centred on the whole panel.
        var rt = (RectTransform)_feedback.transform;
        rt.sizeDelta = new Vector2(visible ? panelSize.x * 0.28f : panelSize.x, rt.sizeDelta.y);
    }

    private void ShowRunChips()
    {
        var cfg = LessonConfig.Get();
        bool passed = engine.LastPassed;

        var tempo = engine.LastTempoRating;
        Color tempoColor = tempo == TempoRating.OnTempo ? perfectColor : passed ? chipYellowColor : wrongColor;
        string tempoWord = tempo == TempoRating.TooSlow ? "too slow" : tempo == TempoRating.TooFast ? "too fast" : "on tempo";

        bool problem = false, shaky = false;
        foreach (var kv in engine.LastRatings)
        {
            if (kv.Value == "problem") problem = true;
            else if (kv.Value == "shaky") shaky = true;
        }
        float acc = engine.LastAccuracy;
        bool accPass = acc >= cfg.passAccuracy - 1e-4f;
        Color accColor = problem || !accPass ? wrongColor : shaky ? chipYellowColor : perfectColor;
        // A run that missed only by a little is yellow, not red.
        if (!problem && !accPass && acc >= cfg.passAccuracy - 0.1f) accColor = chipYellowColor;

        SetChips(true);
        StyleChip(_tempoChip, _tempoChipText, $"Tempo: {tempoWord}", tempoColor);
        StyleChip(_accuracyChip, _accuracyChipText, $"Accuracy {acc * 100f:0}%", accColor);
    }

    private static void StyleChip(Image chip, TMP_Text label, string text, Color color)
    {
        Color fill = color;
        fill.a = 0.55f;
        chip.color = fill;
        label.text = text;
        label.color = Color.white;
    }

    private static Sprite _roundedSprite;

    // A white pill, 9-sliced: the border is the corner radius (21 units = half the chip height, so the ends are fully round).
    private static Sprite RoundedSprite()
    {
        if (_roundedSprite != null) return _roundedSprite;
        const int size = 64, radius = 21;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Distance outside the rounded rectangle's inner core; 0 or less is inside.
                float dx = Mathf.Max(radius - (x + 0.5f), (x + 0.5f) - (size - radius), 0f);
                float dy = Mathf.Max(radius - (y + 0.5f), (y + 0.5f) - (size - radius), 0f);
                float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        _roundedSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                                       new Vector4(radius, radius, radius, radius));
        return _roundedSprite;
    }

    private void BuildChip(string name, float x, float y, float w, float h, out Image chip, out TMP_Text label)
    {
        chip = Box(name, _root, x, y, w, h, Color.clear);
        chip.sprite = RoundedSprite();
        chip.type = Image.Type.Sliced; // corners stay round at any chip size
        var go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        var rt = (RectTransform)go.transform;
        rt.SetParent(chip.transform, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        label = go.GetComponent<TextMeshProUGUI>();
        label.fontSize = 26f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        chip.gameObject.SetActive(false);
    }

    // Clean = green, Shaky = orange, Problem = red; nothing for a bar that was not judged.
    private Color RatingColor(int bar)
    {
        if (!_ratings.TryGetValue(bar, out string rating)) return Color.clear;
        return rating == "problem" ? wrongColor : rating == "shaky" ? offTempoColor : perfectColor;
    }

    // ------------------------------------------------------------------ Sheet music

    // bar: the bar to show (and to frame when active). colorOf: the tint of every other bar on the line.
    private void RefreshSheet(int bar, bool active, Func<int, Color> colorOf)
    {
        int index = sheet.FindIndex(s => s.Contains(bar));
        if (index < 0) index = Mathf.Clamp(_shownSystem, 0, sheet.Count - 1);
        if (index != _shownSystem) ShowSystem(index);

        var system = sheet[index];
        for (int i = 0; i < _barFills.Count; i++)
        {
            int b = system.firstBar + i;
            bool current = active && b == bar;
            Color c = current ? frameColor : colorOf(b);
            c.a = current ? barFillAlpha * 0.4f : c.a * barFillAlpha;
            _barFills[i].color = c;
            if (current) MoveFrame((RectTransform)_barFills[i].transform);
        }
        foreach (var side in _frame) side.gameObject.SetActive(active);
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

    /// <summary>
    /// Call after changing <see cref="sheet"/> / <see cref="sheetAspect"/> (another song): throws the built panel away so the
    /// next event lays it out again with the new sheet strips. Does nothing before the panel was first built.
    /// </summary>
    public void Rebuild()
    {
        _shownSystem = -1;
        if (!_built) return;
        _built = false;
        _barFills.Clear();
        if (_root)
        {
            _root.gameObject.SetActive(false); // Destroy is deferred to the end of the frame
            Destroy(_root.gameObject);
        }
        _root = null;
    }

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
        // The piano roll needs the evaluator's song; the guided lesson shows sheet music or text only.
        float areaHeight = UseSheet ? BuildSheet() : engine != null ? 0f : BuildPianoRoll();

        float below = AreaTop + areaHeight + 15f;
        _next = Text("Next", 0f, below, panelSize.x, 70f, 56f, TextAlignmentOptions.Center);
        _feedback = Text("Feedback", 0f, below + 75f, panelSize.x, 50f, 36f, TextAlignmentOptions.Center);
        _stats = Text("Stats", 0f, below + 130f, panelSize.x, 110f, 26f, TextAlignmentOptions.Top);
        if (engine != null)
        {
            BuildNextRow(below);
            // The verdict is centred in its own box; the chips go to the right of it, so keep that box left-aligned.
            float chipY = below + 75f + 4f, chipH = 42f, gap = 12f;
            float chipW = (panelSize.x * 0.72f - gap * 3f) / 2f;
            float x0 = panelSize.x * 0.28f + gap;
            BuildChip("Tempo Chip", x0, chipY, chipW, chipH, out _tempoChip, out _tempoChipText);
            BuildChip("Accuracy Chip", x0 + chipW + gap, chipY, chipW, chipH, out _accuracyChip, out _accuracyChipText);
        }
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
            var label = Text("Row " + LessonNote.NameOf(kv.Key), 0f, y, LabelWidth - 8f, rowHeight, 20f, TextAlignmentOptions.Right);
            label.text = LessonNote.NameOf(kv.Key);
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
