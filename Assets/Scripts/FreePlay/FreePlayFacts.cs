using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

// What the coach is told in free play: facts the analyzer computed, already in words (note names, chord names, keys).
// The coach only words these; it never sees raw notes. Sent as "free_play" in CoachContext.ToJson.
public class FreePlayFacts
{
    const int MaxChords = 12;
    const int MaxChordsSeen = 8;
    const int NotableSteadyNotes = 8;
    const int NotableChords = 3;

    // Last phrase (null when nothing was played yet).
    public bool hasPhrase;
    public bool phraseInProgress;
    public int phraseNotes;
    public string lowest, highest;          // note names
    public string hands;                    // "likely right hand", ...; null = unknown
    public string steadiness;               // "steady", "a bit uneven", "uneven"; null = too few notes to tell
    public List<string> chords = new List<string>();
    public List<string> romanNumerals;      // same length as chords, null entries = not in the key; null = no clear key
    public string chordProgression;         // only with 3+ chords: "C - F - G - C (I - IV - V - I)"
    public string key;                      // only when clear
    public string songName;                 // only when recognized
    public bool canLearnInLesson;

    // Session.
    public int sessionPhrases, sessionNotes;
    public string sessionRange;             // "C3 to G5"; null = nothing played
    public List<string> songsRecognized = new List<string>(); // names
    public List<string> chordsSeen = new List<string>();
    public string sessionKey;               // rolling key, only when clear

    public List<string> notable;            // why this phrase is worth a comment; null = not sent

    public static FreePlayFacts Build(PhraseAnalysis p, FreePlaySession s, KeyEstimate lastReportedKey, string lessonSongId,
                                      bool inProgress = false, bool withNotable = true)
    {
        var f = new FreePlayFacts();
        if (p != null && p.noteCount > 0)
        {
            f.hasPhrase = true;
            f.phraseInProgress = inProgress;
            f.phraseNotes = p.noteCount;
            f.lowest = SongFormatter.NoteName(p.lowest);
            f.highest = SongFormatter.NoteName(p.highest);
            f.hands = HandsText(p.hands);
            f.steadiness = SteadinessText(p.steadiness);
            if (p.chords != null)
            {
                var chords = p.chords.Where(c => c != null).Take(MaxChords).ToList();
                f.chords = chords.Select(c => c.name).ToList();
                if (p.romanNumerals != null && p.romanNumerals.Count == p.chords.Count)
                    f.romanNumerals = p.romanNumerals.Where((r, i) => p.chords[i] != null).Take(MaxChords).ToList();
                if (f.chords.Count >= NotableChords)
                {
                    f.chordProgression = string.Join(" - ", f.chords);
                    if (f.romanNumerals != null && f.romanNumerals.Any(r => !string.IsNullOrEmpty(r)))
                        f.chordProgression += " (" + string.Join(" - ", f.romanNumerals.Select((r, i) => string.IsNullOrEmpty(r) ? f.chords[i] : r)) + ")";
                }
            }
            if (p.key != null && p.key.IsClear) f.key = p.key.name;
            if (p.song != null)
            {
                f.songName = p.song.songName;
                f.canLearnInLesson = !string.IsNullOrEmpty(lessonSongId) && p.song.songId == lessonSongId;
            }
            if (withNotable && IsNotable(p, lastReportedKey, out var reasons)) f.notable = reasons;
        }

        if (s != null)
        {
            f.sessionPhrases = s.phrases;
            f.sessionNotes = s.notes;
            if (s.notes > 0 && s.lowest <= s.highest)
                f.sessionRange = SongFormatter.NoteName(s.lowest) + " to " + SongFormatter.NoteName(s.highest);
            foreach (var id in s.songsRecognized ?? new List<string>())
                f.songsRecognized.Add(SongName(id));
            f.chordsSeen = (s.chordsSeen ?? new List<string>()).Take(MaxChordsSeen).ToList();
            if (s.rollingKey != null && s.rollingKey.IsClear) f.sessionKey = s.rollingKey.name;
        }
        return f;
    }

    // A phrase is worth a comment when: a song was recognized, 3+ chords, a steady phrase of 8+ notes, or a new clear key.
    public static bool IsNotable(PhraseAnalysis p, KeyEstimate lastReportedKey, out List<string> reasons)
    {
        reasons = new List<string>();
        if (p == null) return false;
        if (p.song != null) reasons.Add("recognized " + p.song.songName);
        if (p.chords != null && p.chords.Count >= NotableChords) reasons.Add("a progression of " + p.chords.Count + " chords");
        if (p.steadiness == Steadiness.Steady && p.noteCount >= NotableSteadyNotes) reasons.Add("a steady phrase of " + p.noteCount + " notes");
        if (p.key != null && p.key.IsClear &&
            (lastReportedKey == null || lastReportedKey.tonic != p.key.tonic || lastReportedKey.major != p.key.major))
            reasons.Add("a new key: " + p.key.name);
        return reasons.Count > 0;
    }

    // The song Main's lesson teaches (PerformanceEvaluator.songId), or null without an evaluator.
    public static string LessonSongId()
    {
        var evaluator = Object.FindAnyObjectByType<PerformanceEvaluator>(FindObjectsInactive.Include);
        return evaluator ? evaluator.songId : null;
    }

    // One line for logs and the debug panel.
    public static string Describe(PhraseAnalysis p)
    {
        if (p == null || p.noteCount == 0) return "(nothing played)";
        var parts = new List<string>
        {
            p.noteCount + " notes " + SongFormatter.NoteName(p.lowest) + "-" + SongFormatter.NoteName(p.highest)
        };
        string hands = HandsText(p.hands);
        if (hands != null) parts.Add(hands);
        parts.Add(SteadinessText(p.steadiness) ?? "too short to rate");
        if (p.chords != null && p.chords.Count > 0)
        {
            string chords = "chords " + string.Join(" ", p.chords.Where(c => c != null).Select(c => c.name));
            if (p.romanNumerals != null) chords += " (" + string.Join(" ", p.romanNumerals.Select(r => r ?? "?")) + ")";
            parts.Add(chords);
        }
        parts.Add(p.key != null ? "key " + p.key.name + (p.key.IsClear ? "" : " (unclear)") : "no key");
        if (p.song != null) parts.Add("song " + p.song.songName + (p.song.transpose != 0 ? $" (transposed {p.song.transpose:+0;-0})" : ""));
        return string.Join(", ", parts);
    }

    public string ToJson()
    {
        var sb = new StringBuilder(384);
        sb.Append('{');
        if (hasPhrase)
        {
            sb.Append("\"last_phrase\":{");
            if (phraseInProgress) sb.Append("\"in_progress\":true,");
            sb.Append("\"notes\":").Append(phraseNotes);
            sb.Append(",\"lowest\":").Append(AIHttp.Quote(lowest)).Append(",\"highest\":").Append(AIHttp.Quote(highest));
            if (hands != null) sb.Append(",\"hands\":").Append(AIHttp.Quote(hands));
            if (steadiness != null) sb.Append(",\"steadiness\":").Append(AIHttp.Quote(steadiness));
            if (chords.Count > 0)
            {
                sb.Append(",\"chords\":");
                Array(sb, chords);
                if (romanNumerals != null && romanNumerals.Any(r => !string.IsNullOrEmpty(r)))
                {
                    sb.Append(",\"roman_numerals\":");
                    Array(sb, romanNumerals);
                }
            }
            if (chordProgression != null) sb.Append(",\"chord_progression\":").Append(AIHttp.Quote(chordProgression));
            if (key != null) sb.Append(",\"key\":").Append(AIHttp.Quote(key));
            if (songName != null)
                sb.Append(",\"song\":{\"name\":").Append(AIHttp.Quote(songName))
                  .Append(",\"can_learn_in_lesson\":").Append(canLearnInLesson ? "true" : "false").Append('}');
            sb.Append("},");
        }

        sb.Append("\"session\":{\"phrases\":").Append(sessionPhrases).Append(",\"notes\":").Append(sessionNotes);
        if (sessionRange != null) sb.Append(",\"range\":").Append(AIHttp.Quote(sessionRange));
        sb.Append(",\"songs_recognized\":");
        Array(sb, songsRecognized);
        sb.Append(",\"chords_seen\":");
        Array(sb, chordsSeen);
        if (sessionKey != null) sb.Append(",\"key\":").Append(AIHttp.Quote(sessionKey));
        sb.Append('}');

        if (notable != null && notable.Count > 0)
        {
            sb.Append(",\"notable\":");
            Array(sb, notable);
        }
        sb.Append('}');
        return sb.ToString();
    }

    static void Array(StringBuilder sb, List<string> items)
    {
        sb.Append('[');
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(items[i] == null ? "null" : AIHttp.Quote(items[i]));
        }
        sb.Append(']');
    }

    static string HandsText(string hands)
    {
        switch (hands)
        {
            case "right": return "likely right hand";
            case "left": return "likely left hand";
            case "both": return "likely both hands";
            default: return null;
        }
    }

    static string SteadinessText(Steadiness s)
    {
        switch (s)
        {
            case Steadiness.Steady: return "steady";
            case Steadiness.ABitUneven: return "a bit uneven";
            case Steadiness.Uneven: return "uneven";
            default: return null;
        }
    }

    static string SongName(string id)
    {
        var entry = SongDatabase.Entry(id);
        return entry != null && !string.IsNullOrEmpty(entry.name) ? entry.name : id;
    }
}
