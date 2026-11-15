#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Editor test driver: walks Main_2's guided lesson with every run passing (as F5 does) up to a measure, and logs each
// coach line in order, so how much the coach talks can be compared before and after a change. Replies are not spoken.
// Start it in Play mode after the lesson started: LessonWalkthrough.Run(4). Run(4, true) keeps speech on and lets every
// count-in play before ending the run (as pressing F5 after "Play!" does), to hear pass cues and tempo changes in order.
public class LessonWalkthrough : MonoBehaviour
{
    public int lastMeasure = 4;
    public bool realTime;

    CurriculumEngine _engine;
    AICoach _coach;
    readonly List<string> _lines = new List<string>();
    int _runs, _linesAtRun;
    float _nextPoll, _ranAt;
    bool _done, _runStarted;
    double _goTime;

    public static LessonWalkthrough Run(int lastMeasure, bool realTime = false)
    {
        var walk = new GameObject("LessonWalkthrough").AddComponent<LessonWalkthrough>();
        walk.lastMeasure = lastMeasure;
        walk.realTime = realTime;
        return walk;
    }

    void Start()
    {
        _engine = FindAnyObjectByType<CurriculumEngine>(FindObjectsInactive.Include);
        _coach = FindAnyObjectByType<AICoach>(FindObjectsInactive.Include);
        if (!_engine || !_coach) { Debug.LogWarning("[Walkthrough] Needs a CurriculumEngine and an AICoach."); Destroy(gameObject); return; }
        if (!realTime) _coach.speakReplies = false; // no TTS: the lesson does not wait for speech
        _coach.OnResponse += OnResponse;
        _engine.OnRunStarted += OnRunStarted;
        Debug.Log($"[Walkthrough] Start: every run passes, up to measure {lastMeasure}.");
    }

    void OnDestroy()
    {
        if (_coach) _coach.OnResponse -= OnResponse;
        if (_engine) _engine.OnRunStarted -= OnRunStarted;
    }

    void OnRunStarted(RunInfo run)
    {
        _runStarted = true;
        _goTime = run.goTime;
        if (!realTime) return;
        double beat = run.countIn.Count > 1 ? run.countIn[1] - run.countIn[0] : run.secondsPerBeat;
        Debug.Log($"[Walkthrough] count-in: {run.countIn.Count} clicks {beat * 1000:0} ms apart, {run.tempoPct}%, {run.hand}, bars {run.bars}");
    }

    void OnResponse(CoachResult r)
    {
        var c = _coach.LastContext?.curriculum;
        string what = CoachContext.TriggerName(r.trigger) + (c != null ? $" {c.step} m{c.measure} {(c.passed ? "pass" : "fail")}" : "");
        string line = string.IsNullOrWhiteSpace(r.say) ? "(silent)" : r.say;
        _lines.Add($"{what} | {line}");
        Debug.Log($"[Walkthrough] {_lines.Count}. {what} | {line}");
    }

    void Update()
    {
        if (_done || Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + 0.3f;
        if (_coach.IsThinking) return;
        // One coach turn per run: wait for it, or the lesson's coach queue drops turns and the transcript is short.
        if (_runs > 0 && _lines.Count <= _linesAtRun && Time.unscaledTime - _ranAt < 15f) return;

        if (_engine.Measure > lastMeasure || _engine.State == CurriculumState.SongComplete) { Finish(); return; }
        if (realTime && !RealTimeReady()) return;
        int before = _lines.Count; // a cue is delivered inside SimulateRun, so count first
        _runStarted = false;
        if (_engine.SimulateRun(null)) { _runs++; _linesAtRun = before; _ranAt = Time.unscaledTime; }
        else _engine.FastForward(); // a demo or count-in: skip to the next run
    }

    // Real time: end a run only once its count-in has played; skip ahead only when no run has started for a while (a demo).
    bool RealTimeReady()
    {
        if (_coach.voiceOutput != null && _coach.voiceOutput.IsSpeaking) return false;
        if (_runStarted) return Time.unscaledTimeAsDouble > _goTime + 0.3;
        return Time.unscaledTime - _ranAt > 25f;
    }

    void Finish()
    {
        _done = true;
        var spoken = _lines.Where(l => !l.EndsWith("| (silent)")).Select(l => l.Substring(l.IndexOf('|') + 2)).ToList();
        int words = spoken.Sum(s => s.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Length);
        Debug.Log($"[Walkthrough] Done: {_runs} runs, {_lines.Count} coach turns, {spoken.Count} spoken, {words} words.");
        _coach.speakReplies = true;
        Destroy(gameObject);
    }
}
#endif
