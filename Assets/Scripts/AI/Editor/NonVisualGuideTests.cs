using NUnit.Framework;

public class NonVisualGuideTests
{
    [TestCase(75, 76, "one key to the right")]
    [TestCase(78, 76, "two keys to the left")]
    [TestCase(64, 76, "an octave higher")]
    [TestCase(84, 76, "lower down")]
    public void Direction_Words(int played, int target, string expected)
    {
        Assert.AreEqual(expected, NonVisualGuide.Direction(played, target));
    }

    [Test]
    public void NoteWords_WithAndWithoutFinger()
    {
        Assert.AreEqual("E, finger five", NonVisualGuide.NoteWords(76, 5));
        Assert.AreEqual("E", NonVisualGuide.NoteWords(76, 0));
    }

    [Test]
    public void ReadNotes_FurEliseBarOne()
    {
        var song = CurriculumSongs.FurElise();
        string text = NonVisualGuide.ReadNotes(song, new BarRange(1, 1), HandMode.Right, 16);
        StringAssert.StartsWith("Right hand: E, finger", text);
        Assert.IsNull(NonVisualGuide.ReadNotes(song, new BarRange(1, 8), HandMode.Both, 16), "too many notes");
    }
}
