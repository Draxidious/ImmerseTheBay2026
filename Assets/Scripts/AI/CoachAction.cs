using System;
using UnityEngine;

// What the coach wants the lesson to do next. Subscribe to AICoach.OnCoachAction and switch on type.
// Every value has already been checked: bars are inside the song, tempo is 40-100, fingers are 1-5.
// Fingerings come from the song data (CoachFacts.Fingering), never from the model.

public enum CoachActionType { None, LoopBars, SetTempo, Demo, ShowFingering, Advance }

public class CoachAction
{
    public const int MinTempoPct = 40;
    public const int MaxTempoPct = 100;
    const int MaxFingers = 32;

    public CoachActionType type = CoachActionType.None;

    // LoopBars, Demo: the bars to repeat or to play for the student. ShowFingering: firstBar == lastBar == the bar.
    public int firstBar;
    public int lastBar;

    // SetTempo: percentage of the song's tempo.
    public int tempoPct;

    // ShowFingering: one finger per note of that hand in the bar, in order. 1 = thumb, 5 = little finger.
    public PianoHand hand;
    public int[] fingers = new int[0];

    public static CoachAction None => new CoachAction();

    // The name the model uses for each action type.
    public static string Name(CoachActionType t)
    {
        switch (t)
        {
            case CoachActionType.LoopBars: return "loop_bars";
            case CoachActionType.SetTempo: return "set_tempo";
            case CoachActionType.Demo: return "demo";
            case CoachActionType.ShowFingering: return "show_fingering";
            case CoachActionType.Advance: return "advance";
            default: return "none";
        }
    }

    public override string ToString()
    {
        switch (type)
        {
            case CoachActionType.LoopBars: return $"loop_bars {firstBar}-{lastBar}";
            case CoachActionType.Demo: return $"demo {firstBar}-{lastBar}";
            case CoachActionType.SetTempo: return $"set_tempo {tempoPct}%";
            case CoachActionType.ShowFingering: return $"show_fingering bar {firstBar} {hand} [{string.Join(" ", fingers)}]";
            case CoachActionType.Advance: return "advance";
            default: return "none";
        }
    }

    // Builds a safe action from whatever the model returned. Anything unusable becomes None.
    internal static CoachAction FromRaw(CoachResult.Raw raw, CoachContext ctx)
    {
        var a = new CoachAction();
        string name = (raw.action ?? "").Trim().ToLowerInvariant();

        // Bars are checked against the song when there is one, otherwise against the current section.
        var section = ctx?.currentSection ?? new SectionInfo();
        int songBars = ctx?.song != null ? ctx.song.BarCount : 0;
        int maxBar = songBars > 0 ? songBars : Mathf.Max(1, section.lastBar);
        int sectionFirst = Mathf.Clamp(section.firstBar, 1, maxBar);
        int sectionLast = Mathf.Clamp(Mathf.Max(section.firstBar, section.lastBar), 1, maxBar);

        switch (name)
        {
            case "loop_bars":
            case "demo":
                a.type = name == "demo" ? CoachActionType.Demo : CoachActionType.LoopBars;
                // No bars given: use the section being practised.
                int from = raw.from_bar > 0 ? raw.from_bar : sectionFirst;
                int to = raw.to_bar > 0 ? raw.to_bar : (raw.from_bar > 0 ? raw.from_bar : sectionLast);
                if (from > to) { int t = from; from = to; to = t; }
                a.firstBar = Mathf.Clamp(from, 1, maxBar);
                a.lastBar = Mathf.Clamp(to, 1, maxBar);
                break;

            case "set_tempo":
                if (raw.tempo_pct <= 0) break;
                a.type = CoachActionType.SetTempo;
                a.tempoPct = Mathf.Clamp(raw.tempo_pct, MinTempoPct, MaxTempoPct);
                break;

            case "show_fingering":
                int bar = raw.from_bar > 0 ? raw.from_bar : (ctx?.stuck != null && ctx.stuck.bar > 0 ? ctx.stuck.bar : sectionFirst);
                bar = Mathf.Clamp(bar, 1, maxBar);
                var hand = (raw.hand ?? "").Trim().ToLowerInvariant().StartsWith("l") ? PianoHand.Left : PianoHand.Right;

                // The model only picks the bar and hand. No known fingering for it = nothing to show.
                int[] known = CoachFacts.Fingering(ctx?.song, bar, hand, section);
                if (known == null || known.Length == 0) break;

                a.type = CoachActionType.ShowFingering;
                a.firstBar = a.lastBar = bar;
                a.hand = hand;
                a.fingers = new int[Mathf.Min(known.Length, MaxFingers)];
                for (int i = 0; i < a.fingers.Length; i++) a.fingers[i] = Mathf.Clamp(known[i], 1, 5);
                break;

            case "advance":
                a.type = CoachActionType.Advance;
                break;
        }

        // The curriculum only lets the coach ask for some actions; it decides the progression itself.
        if (ctx?.allowedActions != null && Array.IndexOf(ctx.allowedActions, a.type) < 0) return new CoachAction();
        return a;
    }
}

// One coach reply: the line to speak plus the action.
public class CoachResult
{
    public string say;
    public CoachAction action = CoachAction.None;
    public CoachTrigger trigger;
    public bool isFallback; // true when the model could not be reached or returned something unusable

    // The JSON object the model is asked for. Unused fields come back as 0 / "".
    [Serializable]
    internal class Raw
    {
        public string say;
        public string action;
        public int from_bar;
        public int to_bar;
        public int tempo_pct;
        public string hand;
    }

    // Safe parse: null when there is no usable "say". A bad or missing action becomes None.
    public static CoachResult Parse(string json, CoachContext ctx)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            // Tolerate code fences or stray text around the object.
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            var raw = JsonUtility.FromJson<Raw>(json.Substring(start, end - start + 1));
            if (raw == null || string.IsNullOrWhiteSpace(raw.say)) return null;

            return new CoachResult
            {
                say = raw.say.Trim(),
                action = CoachAction.FromRaw(raw, ctx),
                trigger = ctx != null ? ctx.trigger : CoachTrigger.RunFinished,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
