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
