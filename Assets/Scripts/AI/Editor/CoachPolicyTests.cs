using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// EditMode tests for CoachPolicy: which lesson turns reach the model, the cues built in code, and tidying the model's line.
public class CoachPolicyTests
{
    static CoachContext Lesson(CoachTrigger trigger, string step, bool passed, int first, int last, string nextStep, bool nothingPlayed = false) =>
        new CoachContext
        {
            trigger = trigger,
            mode = "curriculum",
            currentSection = new SectionInfo { firstBar = first, lastBar = last },
            thisRun = new RunStats { nothingPlayed = nothingPlayed },
            curriculum = new CurriculumInfo { step = step, passed = passed, nextStep = nextStep }
        };

    const string Right = "with the right hand alone", Left = "with the left hand alone", Both = "with both hands together";

    // The contexts CurriculumEngine sends when every run passes, measures 1 to 4 (as LessonWalkthrough runs it in Play mode).
    static IEnumerable<CoachContext> PassingWalk(int measures)
    {
        yield return Lesson(CoachTrigger.LessonStart, "", false, 1, 1, $"First they play measure 1 on its own {Right} at a slow speed.");
        for (int m = 1; m <= measures; m++)
        {
            var hands = m == 1 ? new[] { Right } : new[] { Right, Left, Both };
            for (int h = 0; h < hands.Length; h++)
            {
                yield return Lesson(CoachTrigger.TempoUp, "learn", true, m, m, $"They play measure {m} {hands[h]} again at a medium speed.");
                yield return Lesson(CoachTrigger.TempoUp, "learn", true, m, m, $"They play measure {m} {hands[h]} again at full speed.");
                if (h + 1 < hands.Length)
                    yield return Lesson(CoachTrigger.RunFinished, "learn", true, m, m,
                        $"Measure {m} is learned {hands[h]}. Next they play measure {m} {hands[h + 1]} at a slow speed.");
            }
            if (m == 1)
                yield return Lesson(CoachTrigger.MeasureLearned, "learn", true, 2, 2,
                    $"Measure 1 is learned. Next they learn measure 2 on its own, {Right} first, starting slow.");
            else
            {
                yield return Lesson(CoachTrigger.MeasureLearned, "learn", true, 1, m,
                    $"Measure {m} is learned. Next they play measures 1 to {m} {Both} at full speed.");
                yield return Lesson(CoachTrigger.PlayThroughPassed, "play_through", true, m + 1, m + 1,
                    $"Measures 1 to {m} are solid. Next they learn measure {m + 1} on its own, {Right} first, starting slow.");
            }
        }
    }

    [Test]
    public void PassingWalk_OnlyFullPlayThroughsArePraised()
    {
        var plan = new CoachPolicy.Plan();
        var transcript = new List<string>();
        foreach (var ctx in PassingWalk(4))
        {
            var turn = CoachPolicy.Decide(ctx, plan, out string cue);
            if (ctx.trigger != CoachTrigger.UserAsked) plan = CoachPolicy.PlanOf(ctx);
            bool praise = CoachPolicy.PraiseAllowed(ctx);
            transcript.Add($"{CoachContext.TriggerName(ctx.trigger),-20} {turn,-6} {(praise ? "praise" : ""),-6} {cue}");

            Assert.AreEqual(ctx.trigger == CoachTrigger.PlayThroughPassed, praise, CoachContext.TriggerName(ctx.trigger));
            bool model = ctx.trigger == CoachTrigger.LessonStart || ctx.trigger == CoachTrigger.PlayThroughPassed;
            Assert.AreEqual(model, turn == CoachPolicy.Turn.Model, CoachContext.TriggerName(ctx.trigger) + " " + cue);
            if (cue != null) Assert.LessOrEqual(cue.Split(' ').Length, 5, cue);
        }
        TestContext.WriteLine(string.Join("\n", transcript));
    }

    [Test]
    public void PassingWalk_PassCues()
    {
        var plan = new CoachPolicy.Plan();
        var sounds = new List<string>();
        foreach (var ctx in PassingWalk(2))
        {
            CoachPolicy.Decide(ctx, plan, out string cue);
            plan = CoachPolicy.PlanOf(ctx);
            sounds.Add(PassCues.For(ctx, cue).ToString());
        }
        // Lesson start; m1: 50 -> 75 -> 100 -> learned; m2 per hand: 50 -> 75 -> 100 -> next hand; then the play-through.
        Assert.AreEqual("None Pass Pass Milestone " +
                        "Pass Pass StepUp Pass Pass StepUp Pass Pass Milestone Milestone", string.Join(" ", sounds));
        var failed = Lesson(CoachTrigger.RunFinished, "learn", false, 2, 2, $"They play measure 2 {Right} again at a slow speed.");
        Assert.AreEqual(PassCues.Kind.None, PassCues.For(failed, null));
        Assert.AreEqual(PassCues.Kind.None, PassCues.For(new CoachContext { trigger = CoachTrigger.RunFinished, mode = "freeplay" }, null));
    }

    [Test]
    public void Cues_SayOnlyWhatChanges()
    {
        CoachPolicy.Plan P(int a, int b, string hand) => new CoachPolicy.Plan { known = true, firstBar = a, lastBar = b, hand = hand };
        Assert.IsNull(CoachPolicy.Cue(P(2, 2, "right"), P(2, 2, "right")));              // faster only: the overlay shows it
        Assert.AreEqual("Now the left hand.", CoachPolicy.Cue(P(2, 2, "right"), P(2, 2, "left")));
        Assert.AreEqual("Both hands now.", CoachPolicy.Cue(P(2, 2, "left"), P(2, 2, "both")));
        Assert.AreEqual("Now bar 3.", CoachPolicy.Cue(P(1, 2, "both"), P(3, 3, "right")));
        Assert.AreEqual("Now bars 1 and 2.", CoachPolicy.Cue(P(2, 2, "both"), P(1, 2, "both")));
        Assert.AreEqual("Now bars 1 to 4.", CoachPolicy.Cue(P(4, 4, "both"), P(1, 4, "both")));
        Assert.AreEqual("Back to bar 2.", CoachPolicy.Cue(P(3, 4, "both"), P(2, 2, "right")));   // the next repair block
    }

    [Test]
    public void FailedRun_GoesToTheModelWithoutPraise()
    {
        var ctx = Lesson(CoachTrigger.RunFinished, "learn", false, 3, 3, $"They play measure 3 {Right} again at a slow speed.");
        Assert.AreEqual(CoachPolicy.Turn.Model, CoachPolicy.Decide(ctx, new CoachPolicy.Plan(), out _));
        Assert.IsFalse(CoachPolicy.PraiseAllowed(ctx));
    }

    [Test]
    public void NothingPlayed_IsAShortCue()
    {
        var ctx = Lesson(CoachTrigger.RunFinished, "learn", false, 1, 1, "", nothingPlayed: true);
        Assert.AreEqual(CoachPolicy.Turn.Cue, CoachPolicy.Decide(ctx, new CoachPolicy.Plan(), out string cue));
        Assert.AreEqual("Start when the count-in ends.", cue);
    }

    [Test]
    public void MainWholeExcerpt_IsAFullRun()
    {
        var ctx = new CoachContext { trigger = CoachTrigger.RunFinished, mode = "freeplay" };
        Assert.AreEqual(CoachPolicy.Turn.Model, CoachPolicy.Decide(ctx, new CoachPolicy.Plan(), out _));
        Assert.IsTrue(CoachPolicy.PraiseAllowed(ctx));
        Assert.IsFalse(CoachPolicy.PraiseAllowed(new CoachContext { trigger = CoachTrigger.UserAsked, mode = "freeplay" }));
    }

    [TestCase("Nice try, but keep bar 3 light.", "Keep bar 3 light.")]
    [TestCase("Great job! Bar two had a wrong G, so slow the jump.", "Bar two had a wrong G, so slow the jump.")]
    [TestCase("Lovely, bar four came out clean.", "Bar four came out clean.")]
    [TestCase("Keep the E in bar 2 light.", "Keep the E in bar 2 light.")]
    [TestCase("Nice!", "Nice!")]
    public void StripFiller_RemovesTheOpener(string say, string expected)
    {
        Assert.AreEqual(expected, CoachPolicy.StripFiller(say));
    }

    [Test]
    public void Limit_OneSentenceUnlessAnswering()
    {
        const string two = "Bar three had a wrong C. Count it out loud next time.";
        Assert.AreEqual("Bar three had a wrong C.", CoachPolicy.Limit(two, false));
        Assert.AreEqual(two, CoachPolicy.Limit(two, true));
    }

    // Failed-run replies from the real model (Main_2, measure 3) that ran over 80 characters.
    [TestCase("Bar three is still too slow to feel like a phrase; try a little faster, and watch the ring finger on the last B.",
              "Bar three is still too slow to feel like a phrase; try a little faster.")]
    [TestCase("Bar three's last B keeps slipping; the ring finger plays it, so let me show you the fingers.",
              "Bar three's last B keeps slipping; the ring finger plays it.")]
    [TestCase("Bar three's B keeps slipping; use your ring finger for it and go slowly.",
              "Bar three's B keeps slipping; use your ring finger for it and go slowly.")]
    public void Limit_CutsALongSentenceAtAClause(string say, string expected)
    {
        string kept = CoachPolicy.Limit(say, false);
        Assert.AreEqual(expected, kept);
        Assert.LessOrEqual(kept.Length, CoachPolicy.MaxChars);
    }

    [Test]
    public void RepeatsOpening_MatchesTheFirstFourWords()
    {
        var recent = new[] { "Bar four came out clean and steady.", "Watch the jump to the low A." };
        Assert.IsTrue(CoachPolicy.RepeatsOpening("Bar four came out with one slip.", recent));
        Assert.IsTrue(CoachPolicy.RepeatsOpening("watch the jump, to E5 this time", recent));
        Assert.IsFalse(CoachPolicy.RepeatsOpening("Bar three came out clean.", recent));
        Assert.IsFalse(CoachPolicy.RepeatsOpening("Again.", recent));
    }
}
