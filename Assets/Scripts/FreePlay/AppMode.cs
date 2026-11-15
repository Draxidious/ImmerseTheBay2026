using System;
using UnityEngine;

// Lesson = the wait-mode Für Elise lesson. FreePlay = play anything; the coach comments on what it hears.
public enum AppMode { Lesson, FreePlay }

// AIConfig.defaultMode: what happens after calibration. Ask = show the Lesson / Free Play panel.
public enum StartMode { Ask, Lesson, FreePlay }

// Mode switch requests from the panel, voice commands and the debug panel. AppFlow handles Requested (shows/hides the
// lesson, calls FreePlayController.Begin/End) and keeps Current and AfterCalibration up to date.
public static class AppModeSwitch
{
    // The mode being played now; null before one is chosen.
    public static AppMode? Current { get; set; }

    // True once the first calibration is done (choosing a mode, the lesson, free play). QuestionRouter listens then.
    public static bool AfterCalibration { get; set; }

    // mode, source ("panel", "voice", "debug", ...).
    public static event Action<AppMode, string> Requested;

    // "Teach me <song>": start the guided lesson on that song (CurriculumSongs.Teachable). songId, source.
    public static event Action<string, string> LearnRequested;

    public static void RequestLearn(string songId, string source)
    {
        Debug.Log($"[FreePlay] Lesson on {songId} requested ({source}){(LearnRequested == null ? ", nobody handles it yet" : "")}.");
        LearnRequested?.Invoke(songId, source);
    }

    public static bool HasHandler => Requested != null;

    public static void Request(AppMode mode, string source)
    {
        Debug.Log($"[FreePlay] Mode {mode} requested ({source}){(Requested == null ? ", nobody handles it yet" : "")}.");
        Requested?.Invoke(mode, source);
    }
}
