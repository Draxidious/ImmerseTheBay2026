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

    [TestCase(64, "the white key just right of the two black keys")]
    [TestCase(75, "the right one of the two black keys")]
    [TestCase(57, "the white key between the last two of the three black keys")]
    public void Landmark_Words(int midi, string expected)
    {
        Assert.AreEqual(expected, NonVisualGuide.Landmark(midi));
    }

    [Test]
    public void ReadNotes_FurEliseBarOne()
    {
        var song = CurriculumSongs.FurElise();
        string text = NonVisualGuide.ReadNotes(song, new BarRange(1, 1), HandMode.Right, 16);
        StringAssert.StartsWith("Right hand: E, finger", text);
        Assert.AreEqual(1, text.Split("finger").Length - 1, "a finger only on the bar's first note: " + text);
        StringAssert.Contains(", D sharp, E, D sharp,", text);
        Assert.IsNull(NonVisualGuide.ReadNotes(song, new BarRange(1, 8), HandMode.Both, 16), "too many notes");
    }
}
