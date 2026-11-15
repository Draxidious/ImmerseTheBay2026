using NUnit.Framework;

// EditMode tests for the free-play glue outside the analyzer: voice mode commands and the reply length guard.
public class FreePlayCoachTests
{
    [TestCase("free play", AppMode.FreePlay)]
    [TestCase("Free play mode!", AppMode.FreePlay)]
    [TestCase("switch to free-play mode", AppMode.FreePlay)]
    [TestCase("lesson mode.", AppMode.Lesson)]
    [TestCase("Back to the lesson.", AppMode.Lesson)]
    public void ModeCommand_Recognized(string text, AppMode expected)
    {
        Assert.IsTrue(QuestionRouter.TryParseModeCommand(text, out var mode));
        Assert.AreEqual(expected, mode);
    }

    [TestCase("What is a lesson?")]
    [TestCase("lesson plan")]
    [TestCase("what key am I playing in")]
    public void ModeCommand_NotAQuestion(string text)
    {
        Assert.IsFalse(QuestionRouter.TryParseModeCommand(text, out _));
    }

    [TestCase("Yes.")]
    [TestCase("Yeah, sure!")]
    [TestCase("Okay, let's do it.")]
    [TestCase("yes please")]
    public void ConfirmFreePlay_Yes(string text)
    {
        Assert.IsTrue(QuestionRouter.IsYes(text));
    }

    [TestCase("No.")]
    [TestCase("Okay, no thanks.")]
    [TestCase("Let's keep going with the lesson.")]
    [TestCase("Not now")]
    [TestCase("What is free play?")]
    [TestCase("")]
    public void ConfirmFreePlay_NotYes(string text)
    {
        Assert.IsFalse(QuestionRouter.IsYes(text));
    }

    [TestCase("Play it again.")]
    [TestCase("play that again")]
    [TestCase("Can you play that again please?")]
    [TestCase("Play this one more time.")]
    [TestCase("Show me again.")]
    [TestCase("Play it for me again")]
    public void ReplayCommand_Recognized(string text)
    {
        Assert.IsTrue(QuestionRouter.IsReplayCommand(text));
    }

    [TestCase("Why do I play it again so slowly?")]
    [TestCase("Play free play")]
    [TestCase("How do I play this part?")]
    [TestCase("")]
    public void ReplayCommand_NotACommand(string text)
    {
        Assert.IsFalse(QuestionRouter.IsReplayCommand(text));
    }

    [Test]
    public void KeepWholeSentences_DropsTheSentenceThatOverflows()
    {
        const string reply = "That was lovely, a I, IV, V, I progression in C major, so you've just walked through a full little cadence. " +
                             "Nicely played, and the chords landed clearly.";
        string kept = AICoach.KeepWholeSentences(reply, 110);
        Assert.AreEqual("That was lovely, a I, IV, V, I progression in C major, so you've just walked through a full little cadence.", kept);
        Assert.LessOrEqual(kept.Length, 110);
    }

    [Test]
    public void KeepWholeSentences_ShortReplyUnchanged()
    {
        const string reply = "Nice, C, F, G and back to C. What will you try next?";
        Assert.AreEqual(reply, AICoach.KeepWholeSentences(reply, 110));
    }
}
