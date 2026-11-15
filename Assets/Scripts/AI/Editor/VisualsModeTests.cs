using NUnit.Framework;

public class VisualsModeTests
{
    [TestCase("Visuals off.", false)]
    [TestCase("visual off", false)]
    [TestCase("Turn off the visuals, please.", false)]
    [TestCase("turn the visuals off", false)]
    [TestCase("hide the visuals", false)]
    [TestCase("audio only", false)]
    [TestCase("Non-visual mode", false)]
    [TestCase("Visuals on!", true)]
    [TestCase("okay, turn on the visuals", true)]
    [TestCase("show me the visuals", true)]
    [TestCase("visual mode", true)]
    [TestCase("visuals mode on", true)]
    public void Command_Recognized(string text, bool on)
    {
        Assert.IsTrue(VisualsMode.TryParseCommand(text, out bool visualsOn), text);
        Assert.AreEqual(on, visualsOn, text);
    }

    [TestCase("Yes, audio only.", true)]
    [TestCase("voice only please", true)]
    [TestCase("I'd like no visuals", true)]
    [TestCase("Yes, I'm ready!", false)]
    [TestCase("no", false)]
    [TestCase("sure, let's go with the visual one", false)]
    public void IntroAnswer_AsksForNonVisual(string text, bool expected)
    {
        Assert.AreEqual(expected, VisualsMode.AsksForNonVisual(text), text);
    }

    [TestCase("")]
    [TestCase("what are visuals")]
    [TestCase("lesson mode")]
    [TestCase("show me the music for jingle bells")]
    [TestCase("turn it off")]
    public void Other_NotACommand(string text)
    {
        Assert.IsFalse(VisualsMode.TryParseCommand(text, out _), text);
    }
}
