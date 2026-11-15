using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// Which coach turns reach the model, and the short lines built in code for the rest. Praise is only for a full run:
// a passed measure, tempo step or repair block gets a few words saying what changes ("Now bar 3.") or nothing.
public static class CoachPolicy
{
    public enum Turn { Model, Cue, Silent }

    // What the next lesson run plays, from a CurriculumEngine context. AICoach keeps the last one to see what changes.
    public struct Plan
    {
        public bool known;
        public int firstBar, lastBar;
        public string hand; // "right", "left", "both" or null
    }

    public const int MaxChars = 80;
    public const int QuestionMaxChars = 180;
    public const int NewMeasureMaxChars = 50; // lesson start and play-through: the bars and one detail, the overlay shows the rest

    // A full run: Main's whole excerpt in wait mode, a passed play-through of bars 1..N, or the finished piece.
    public static bool PraiseAllowed(CoachContext ctx) =>
        ctx != null &&
        (ctx.trigger == CoachTrigger.PlayThroughPassed || ctx.trigger == CoachTrigger.SongComplete ||
         (ctx.mode == "freeplay" && ctx.trigger == CoachTrigger.RunFinished));

    // previous: the plan of the run that just ended (what the context before this one announced).
    public static Turn Decide(CoachContext ctx, Plan previous, out string cue)
    {
        cue = null;
        switch (ctx.trigger)
        {
            case CoachTrigger.UserAsked:
            case CoachTrigger.PhraseEnded:
            case CoachTrigger.Stuck:
            case CoachTrigger.LessonStart:
            case CoachTrigger.Help:
            case CoachTrigger.PlayThroughPassed:
            case CoachTrigger.SongComplete:
                return Turn.Model;
        }
        var c = ctx.curriculum;
        if (ctx.mode != "curriculum" || c == null) return Turn.Model;

        if (ctx.thisRun != null && ctx.thisRun.nothingPlayed) cue = "Start when the count-in ends.";
        else if (ctx.trigger == CoachTrigger.SetAside) cue = "Let's leave that for now.";
        else if (ctx.trigger == CoachTrigger.WarmupResult && c.passed) cue = null; // the song intro follows
        else if (c.passed) cue = Cue(previous, PlanOf(ctx));
        else return Turn.Model; // a failed run: one fix from the model
        return string.IsNullOrEmpty(cue) ? Turn.Silent : Turn.Cue;
    }

    public static Plan PlanOf(CoachContext ctx)
    {
        var s = ctx.currentSection ?? new SectionInfo();
        return new Plan
        {
            known = ctx.curriculum != null && s.firstBar > 0,
            firstBar = s.firstBar,
            lastBar = s.lastBar,
            hand = HandIn(ctx.curriculum?.nextStep)
        };
    }

    // New bars are named; a new hand is named; the same bars and hand (only the speed changes) need no words.
    public static string Cue(Plan previous, Plan next)
    {
        if (!next.known) return null;
        if (!previous.known || previous.firstBar != next.firstBar || previous.lastBar != next.lastBar)
        {
            bool playThrough = next.firstBar == 1 && next.lastBar > 1;
            bool back = previous.known && next.firstBar < previous.firstBar && !playThrough;
            return (back ? "Back to " : "Now ") + Bars(next.firstBar, next.lastBar) + ".";
        }
        if (next.hand != null && next.hand != previous.hand)
            return next.hand == "both" ? "Both hands now." : "Now the " + next.hand + " hand.";
        return null;
    }

    static string Bars(int first, int last) =>
        first == last ? "bar " + first : last == first + 1 ? $"bars {first} and {last}" : $"bars {first} to {last}";

    // The hand of the next run: the last hand named in CurriculumInfo.nextStep ("... with the left hand alone ...").
    static string HandIn(string nextStep)
    {
        if (string.IsNullOrEmpty(nextStep)) return null;
        var found = new[] { ("both hands", "both"), ("left hand", "left"), ("right hand", "right") }
            .Select(h => (at: nextStep.LastIndexOf(h.Item1, System.StringComparison.OrdinalIgnoreCase), hand: h.Item2))
            .Where(h => h.at >= 0).OrderByDescending(h => h.at).ToList();
        return found.Count > 0 ? found[0].hand : null;
    }

    // ------------------------------------------------------------------ Tidying the model's line

    static readonly Regex FillerOpener = new Regex(
        @"^(?:(?:nice|great|good|lovely|beautiful|awesome|excellent|perfect|wonderful|fantastic|brilliant|amazing|super|sweet|" +
        @"well done|nice work|great job|good job|nice job|great work|good work|nice try|good try|good effort|great question|" +
        @"good question|okay|ok|alright|all right)\b[^,.!?]{0,24}[,.!?]+\s*)+(?:but|and|so)?\s*",
        RegexOptions.IgnoreCase);

    // "Nice try, but keep bar 3 light." -> "Keep bar 3 light." Leaves the line alone if little would be left.
    public static string StripFiller(string say)
    {
        if (string.IsNullOrWhiteSpace(say)) return say;
        string rest = FillerOpener.Replace(say.Trim(), "", 1);
        if (rest.Split(' ').Length < 3) return say.Trim();
        return char.ToUpperInvariant(rest[0]) + rest.Substring(1);
    }

    // One sentence of up to MaxChars; a "hey teacher" answer may have two. Always at least the first sentence.
    public static string Limit(string say, bool question) =>
        Limit(say, question ? 2 : 1, question ? QuestionMaxChars : MaxChars);

    public static string Limit(string say, int maxSentences, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(say)) return say;
        var parts = Regex.Split(say.Trim(), @"(?<=[.!?])\s+");
        string kept = parts[0];
        for (int i = 1; i < parts.Length && i < maxSentences && kept.Length + 1 + parts[i].Length <= maxChars; i++) kept += " " + parts[i];
        return kept.Length > maxChars ? CutAtClause(kept, maxChars) : kept;
    }

    static readonly Regex ClauseBreak = new Regex(@"(;|,\s+(?:and|but|so|then)\b)\s*", RegexOptions.IgnoreCase);

    // A sentence over max ends at its last clause break that fits ("...; try a little faster, and watch ..." keeps the
    // first fix). Unchanged when no break fits; TTS still reads it, it is only longer.
    static string CutAtClause(string sentence, int max)
    {
        int cut = -1;
        foreach (Match m in ClauseBreak.Matches(sentence))
            if (m.Index <= max - 1 && m.Index >= 20) cut = m.Index;
        return cut < 0 ? sentence : sentence.Substring(0, cut).TrimEnd() + ".";
    }

    public static string Tidy(string say, bool question) => Limit(StripFiller(say), question);

    // Per trigger: a question may have two sentences; the lines before a new measure are kept shortest.
    public static string Tidy(string say, CoachTrigger trigger)
    {
        if (trigger == CoachTrigger.UserAsked) return Tidy(say, true);
        bool newMeasure = trigger == CoachTrigger.LessonStart || trigger == CoachTrigger.PlayThroughPassed;
        return Limit(StripFiller(say), 1, newMeasure ? NewMeasureMaxChars : MaxChars);
    }

    // True when the line starts with the same words as one of the recent lines.
    public static bool RepeatsOpening(string say, IEnumerable<string> recent, int words = 4)
    {
        var start = Opening(say, words);
        if (start == null || recent == null) return false;
        return recent.Any(r => Opening(r, words) == start);
    }

    static string Opening(string text, int words)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var w = Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9']+").Cast<Match>().Select(m => m.Value).Take(words).ToList();
        return w.Count < words ? null : string.Join(" ", w);
    }
}
