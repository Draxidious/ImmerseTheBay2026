using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Everything the coach is told about the lesson right now. The lesson engine fills one of these and calls AICoach.Coach.
// Notes are MIDI note numbers, bars are 1-based.

public enum CoachTrigger
{
    LessonStart, RunFinished, Stuck, UserAsked,
    // Curriculum transitions (CurriculumEngine). Progression is decided in code, the coach only words it.
    WarmupResult, ChunkPassed, ChunkFailed, DrillStart, DrillPassed, DrillSkipped, SongComplete, TempoUp, TempoDown,
}

[Serializable]
public class WrongNote
{
    public int bar;
    public int expected; // MIDI note
    public int played;   // MIDI note
}

[Serializable]
public class Hesitation
{
    public int bar;
    public int note;     // MIDI note
    public int ms;       // time from the note becoming "next" to the correct key press
}

[Serializable]
public class StuckInfo
{
    public int bar;
    public int note;     // the note they can't get
    public int ms;       // how long they have been on it
    public int misses;   // wrong keys pressed while on it
}

[Serializable]
public class SectionInfo
{
    public int firstBar = 1;
    public int lastBar = 1;
    public int tempoPct = 100; // playback tempo as a percentage of the song's tempo
}

[Serializable]
public class RunStats
{
    public float accuracy; // 0-100
    public List<WrongNote> wrongNotes = new List<WrongNote>();
    public List<Hesitation> hesitations = new List<Hesitation>();
    public List<int> rushedBars = new List<int>();
    public bool timingChecked = true; // false when timing does not count for this run, so it is never praised
    public float timing = -1f;         // 0-100 timing score, -1 = not measured
    public List<int> offBeatBars = new List<int>(); // bars whose timing was below the bar, when timing counts
}

[Serializable]
public class LessonHistory
{
    public int attemptsThisSection;
    public List<float> accuracyTrend = new List<float>(); // oldest first, the last 5 are sent
}

// Where the curriculum is and what it decided. Filled by CurriculumEngine for the curriculum triggers.
[Serializable]
public class CurriculumInfo
{
    public string stage = "song";      // "warmup" or "song"
    public bool passed;                // the run that just ended
    public int chunkFirstBar;          // the chunk being built up, 0 = none
    public int chunkLastBar;
    public int chunkNumber;            // 1-based
    public int chunkCount;
    public int drillBar;               // 0 = not drilling
    public int attempt;                // tries at the current chunk, drill bar or warm-up, including this one
    public string escalation = "";     // drill only: "demo", "slower", "skip" or ""
    public List<int> failedBars = new List<int>();
    public List<int> skippedBars = new List<int>();
    public string nextStep = "";       // what the app does next, in plain words
    public bool clean;                 // the run that just ended was clean
    public int cleanStreak;            // clean runs in a row at this tempo step so far
    public int cleanNeeded;            // clean runs in a row needed to pass the step
    public LessonStats stats;          // song_complete only
}

[Serializable]
public class LessonStats
{
    public int warmupAttempts;
    public int runs;                   // every chunk and drill run of the song
    public int chunksPassed;
    public int chunksPassedFirstTry;
    public int tempoDrops;
    public List<int> drilledBars = new List<int>();
    public List<int> skippedBars = new List<int>();
    public float finalRunAccuracy;     // 0-100, the last full run
    public float minutes;
}

[Serializable]
public class CoachContext
{
    // Only hesitations at least this long are worth telling the coach about, slowest first.
    const int NotableHesitationMs = 1500;
    const int MaxHesitations = 5;
    const int MaxWrongNotes = 12;

    public CoachTrigger trigger = CoachTrigger.RunFinished;
    public string mode = "curriculum"; // "curriculum" or "freeplay"
    public SongData song;              // optional, but bars can only be checked against the song when it is set
    public SectionInfo currentSection = new SectionInfo();
    public RunStats thisRun = new RunStats();
    public LessonHistory history = new LessonHistory();
    public string lastFeedback;        // left empty, AICoach fills in what it said last
    public StuckInfo stuck;            // set for the Stuck trigger
    public string userQuestion;        // set for the UserAsked trigger
    public CurriculumInfo curriculum;  // set by CurriculumEngine
    public int currentTempoPct = 100;  // the practice tempo; the curriculum owns it, the coach can't change it
    public string lastRunSpeed;        // "a slow speed", "a medium speed", "full speed": the run that just ended
    public string nextRunSpeed;        // the same for the next run
    public int tempoStep;              // 1-based step on the tempo ladder, 0 = no ladder
    public int tempoStepCount;
    public bool timingRequired;        // whether timing counts towards passing this step
    public CoachActionType[] allowedActions; // null = any action; anything else the model picks becomes None

    public static string TriggerName(CoachTrigger t)
    {
        switch (t)
        {
            case CoachTrigger.LessonStart: return "lesson_start";
            case CoachTrigger.Stuck: return "stuck";
            case CoachTrigger.UserAsked: return "user_asked";
            case CoachTrigger.WarmupResult: return "warmup_result";
            case CoachTrigger.ChunkPassed: return "chunk_passed";
            case CoachTrigger.ChunkFailed: return "chunk_failed";
            case CoachTrigger.DrillStart: return "drill_start";
            case CoachTrigger.DrillPassed: return "drill_passed";
            case CoachTrigger.DrillSkipped: return "drill_skipped";
            case CoachTrigger.SongComplete: return "song_complete";
            case CoachTrigger.TempoUp: return "tempo_up";
            case CoachTrigger.TempoDown: return "tempo_down";
            default: return "run_finished";
        }
    }

    // Shallow copy: the song, run and history objects are shared with the original.
    public CoachContext Clone() => (CoachContext)MemberwiseClone();

    // The JSON the model reads. Note numbers become names (E4) so they match the song text.
    public string ToJson()
    {
        var sb = new StringBuilder(512);
        sb.Append("{\"trigger\":").Append(AIHttp.Quote(TriggerName(trigger)));
        sb.Append(",\"mode\":").Append(AIHttp.Quote(string.IsNullOrEmpty(mode) ? "curriculum" : mode));

        var s = currentSection ?? new SectionInfo();
        sb.Append(",\"current_section\":{\"bars\":[").Append(s.firstBar).Append(',').Append(s.lastBar)
          .Append("],\"tempo_pct\":").Append(s.tempoPct).Append('}');

        if (trigger != CoachTrigger.LessonStart)
        {
            var r = thisRun ?? new RunStats();
            sb.Append(",\"this_run\":{\"accuracy\":").Append(Number(r.accuracy));

            sb.Append(",\"wrong_notes\":[");
            Join(sb, (r.wrongNotes ?? new List<WrongNote>()).Take(MaxWrongNotes), w =>
                "{\"bar\":" + w.bar + ",\"expected\":" + AIHttp.Quote(SongFormatter.NoteName(w.expected)) +
                ",\"played\":" + AIHttp.Quote(SongFormatter.NoteName(w.played)) +
                ",\"played_is\":" + AIHttp.Quote(CoachFacts.PlayedIs(w.expected, w.played)) + "}");

            sb.Append("],\"hesitations\":[");
            Join(sb, (r.hesitations ?? new List<Hesitation>()).Where(h => h.ms >= NotableHesitationMs)
                    .OrderByDescending(h => h.ms).Take(MaxHesitations), h =>
                "{\"bar\":" + h.bar + ",\"note\":" + AIHttp.Quote(SongFormatter.NoteName(h.note)) + ",\"ms\":" + h.ms + "}");

            sb.Append("],\"rushed_bars\":[");
            Join(sb, r.rushedBars ?? new List<int>(), b => b.ToString());
            sb.Append(']');
            if (r.timing >= 0f) sb.Append(",\"timing\":").Append(Number(r.timing));
            if (r.offBeatBars != null && r.offBeatBars.Count > 0)
            {
                sb.Append(",\"off_beat_bars\":[");
                Join(sb, r.offBeatBars, b => b.ToString());
                sb.Append(']');
            }
            sb.Append('}');
        }

        var hist = history ?? new LessonHistory();
        var trend = hist.accuracyTrend ?? new List<float>();
        sb.Append(",\"history\":{\"attempts_this_section\":").Append(hist.attemptsThisSection).Append(",\"accuracy_trend\":[");
        Join(sb, trend.Skip(Math.Max(0, trend.Count - 5)), Number);
        sb.Append("]}");

        sb.Append(",\"last_feedback\":").Append(AIHttp.Quote(lastFeedback ?? ""));

        if (trigger == CoachTrigger.Stuck && stuck != null)
            sb.Append(",\"stuck\":{\"bar\":").Append(stuck.bar).Append(",\"note\":").Append(AIHttp.Quote(SongFormatter.NoteName(stuck.note)))
              .Append(",\"ms\":").Append(stuck.ms).Append(",\"misses\":").Append(stuck.misses)
              .Append(",\"how_to_find\":").Append(AIHttp.Quote(CoachFacts.HowToFind(song, stuck.bar, stuck.note))).Append('}');

        if (trigger == CoachTrigger.UserAsked)
            sb.Append(",\"user_question\":").Append(AIHttp.Quote(userQuestion ?? ""));

        if (tempoStep > 0)
        {
            // Speeds as words: the model must never say the number, and it mixed up which run pct was for.
            sb.Append(",\"tempo\":{");
            if (!string.IsNullOrEmpty(lastRunSpeed)) sb.Append("\"this_run\":").Append(AIHttp.Quote(lastRunSpeed)).Append(',');
            sb.Append("\"next_run\":").Append(AIHttp.Quote(nextRunSpeed ?? "")).Append(",\"step\":").Append(tempoStep)
              .Append(",\"of\":").Append(tempoStepCount).Append(",\"timing_required\":").Append(timingRequired ? "true" : "false").Append('}');
        }
        if (curriculum != null) AppendCurriculum(sb, curriculum);
        if (allowedActions != null)
        {
            sb.Append(",\"allowed_actions\":[");
            Join(sb, allowedActions, a => AIHttp.Quote(CoachAction.Name(a)));
            sb.Append(']');
        }

        sb.Append(CoachFacts.ToJson(this));
        sb.Append('}');
        return sb.ToString();
    }

    static void AppendCurriculum(StringBuilder sb, CurriculumInfo c)
    {
        sb.Append(",\"curriculum\":{\"stage\":").Append(AIHttp.Quote(c.stage ?? ""));
        sb.Append(",\"passed\":").Append(c.passed ? "true" : "false");
        if (c.chunkFirstBar > 0)
            sb.Append(",\"chunk\":{\"bars\":[").Append(c.chunkFirstBar).Append(',').Append(c.chunkLastBar)
              .Append("],\"number\":").Append(c.chunkNumber).Append(",\"of\":").Append(c.chunkCount).Append('}');
        if (c.drillBar > 0) sb.Append(",\"drill_bar\":").Append(c.drillBar);
        sb.Append(",\"attempt\":").Append(c.attempt);
        if (!string.IsNullOrEmpty(c.escalation)) sb.Append(",\"escalation\":").Append(AIHttp.Quote(c.escalation));
        sb.Append(",\"failed_bars\":[");
        Join(sb, c.failedBars ?? new List<int>(), b => b.ToString());
        sb.Append("],\"skipped_bars\":[");
        Join(sb, c.skippedBars ?? new List<int>(), b => b.ToString());
        sb.Append("],\"next_step\":").Append(AIHttp.Quote(c.nextStep ?? ""));
        sb.Append(",\"clean\":").Append(c.clean ? "true" : "false")
          .Append(",\"clean_streak\":").Append(c.cleanStreak).Append(",\"clean_needed\":").Append(c.cleanNeeded);

        var st = c.stats;
        if (st != null)
        {
            sb.Append(",\"lesson_stats\":{\"warmup_attempts\":").Append(st.warmupAttempts)
              .Append(",\"runs\":").Append(st.runs)
              .Append(",\"chunks_passed\":").Append(st.chunksPassed)
              .Append(",\"chunks_passed_first_try\":").Append(st.chunksPassedFirstTry)
              .Append(",\"tempo_drops\":").Append(st.tempoDrops)
              .Append(",\"drilled_bars\":[");
            Join(sb, st.drilledBars ?? new List<int>(), b => b.ToString());
            sb.Append("],\"skipped_bars\":[");
            Join(sb, st.skippedBars ?? new List<int>(), b => b.ToString());
            sb.Append("],\"final_run_accuracy\":").Append(Number(st.finalRunAccuracy))
              .Append(",\"minutes\":").Append(Math.Round(st.minutes, 1).ToString(CultureInfo.InvariantCulture)).Append('}');
        }
        sb.Append('}');
    }

    static string Number(float f) => Math.Round(f).ToString(CultureInfo.InvariantCulture);

    static void Join<T>(StringBuilder sb, IEnumerable<T> items, Func<T, string> write)
    {
        bool first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(',');
            sb.Append(write(item));
            first = false;
        }
    }
}
