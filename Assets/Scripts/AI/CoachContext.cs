using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Everything the coach is told about the lesson right now. The lesson engine fills one of these and calls AICoach.Coach.
// Notes are MIDI note numbers, bars are 1-based.

public enum CoachTrigger { LessonStart, RunFinished, Stuck, UserAsked }

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
}

[Serializable]
public class LessonHistory
{
    public int attemptsThisSection;
    public List<float> accuracyTrend = new List<float>(); // oldest first, the last 5 are sent
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

    public static string TriggerName(CoachTrigger t)
    {
        switch (t)
        {
            case CoachTrigger.LessonStart: return "lesson_start";
            case CoachTrigger.Stuck: return "stuck";
            case CoachTrigger.UserAsked: return "user_asked";
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
            sb.Append("]}");
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

        sb.Append(CoachFacts.ToJson(this));
        sb.Append('}');
        return sb.ToString();
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
