using System;
using System.Collections.Generic;

// Data shapes shared between the AI layer and the rest of the app.
// Teammates: fill a PerformanceMetrics and pass it to AICoach.GetFeedback / GetCoachResponse.

[Serializable]
public class WrongNote
{
    public int bar;
    public string expectedNote; // e.g. "E4"
    public string playedNote;   // e.g. "F4"
}

[Serializable]
public class PerformanceMetrics
{
    public string songName;
    public List<WrongNote> wrongNotes = new List<WrongNote>();
    public float avgTimingErrorMs;
    public List<int> rushedBars = new List<int>();
    public List<int> draggedBars = new List<int>();
    public float accuracyPercent; // 0-100
}

public enum CoachAction { None, LoopBar, SlowDown, NextSong }

// What the coach model returns: {"say": "...", "action": "loop_bar" | "slow_down" | "next_song" | "none", "bar": 3}
[Serializable]
public class CoachResponse
{
    public string say;
    public string action = "none";
    public int bar;

    public CoachAction Action
    {
        get
        {
            switch (action)
            {
                case "loop_bar": return CoachAction.LoopBar;
                case "slow_down": return CoachAction.SlowDown;
                case "next_song": return CoachAction.NextSong;
                default: return CoachAction.None;
            }
        }
    }

    // Safe parse: returns null on anything that isn't a usable {say, action, bar} object.
    public static CoachResponse Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            // Tolerate code fences or stray text around the object.
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            var r = UnityEngine.JsonUtility.FromJson<CoachResponse>(raw.Substring(start, end - start + 1));
            if (r == null || string.IsNullOrWhiteSpace(r.say)) return null;

            r.action = (r.action ?? "none").Trim().ToLowerInvariant();
            if (r.Action == CoachAction.None) r.action = "none";
            return r;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
