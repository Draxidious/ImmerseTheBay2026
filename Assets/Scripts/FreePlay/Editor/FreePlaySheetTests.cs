using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// EditMode tests for free play's sheet music: following the player through a song, and picking the song by voice.
public class FreePlaySheetTests
{
    // Mary Had a Little Lamb, one note per beat, 4 per bar: E D C D | E E E - | D D D - | E G G -
    static SongData Mary()
    {
        int[][] bars = { new[] { 64, 62, 60, 62 }, new[] { 64, 64, 64 }, new[] { 62, 62, 62 }, new[] { 64, 67, 67 } };
        var song = new SongData { id = "mary", name = "Mary", beatsPerBar = 4 };
        for (int b = 0; b < bars.Length; b++)
            for (int i = 0; i < bars[b].Length; i++)
                song.notes.Add(new SongNote { midi = bars[b][i], bar = b + 1, beat = 1 + i });
        return song;
    }

    [Test]
    public void Follower_MovesThroughTheBars()
    {
        var f = new SheetFollower(Mary());
        var bars = new List<int>();
        foreach (int midi in new[] { 64, 62, 60, 62, 64, 64, 64, 62, 62, 62, 64, 67, 67 }) { f.NoteOn(midi); bars.Add(f.CurrentBar); }
        Assert.AreEqual("1 1 1 1 2 2 2 3 3 3 4 4 4", string.Join(" ", bars));
    }

    [Test]
    public void Follower_IgnoresWrongNotes()
    {
        var f = new SheetFollower(Mary());
        foreach (int midi in new[] { 64, 62, 61, 60, 30, 62, 64 }) f.NoteOn(midi); // C#4 and a low F# are wrong
        Assert.AreEqual(2, f.CurrentBar);
    }

    [Test]
    public void Follower_FindsThePlayerAfterAJump()
    {
        var f = new SheetFollower(Mary());
        foreach (int midi in new[] { 64, 62 }) f.NoteOn(midi);           // bar 1
        foreach (int midi in new[] { 62, 62, 64, 67 }) f.NoteOn(midi);   // jumps to "D D | E G" (bars 3-4)
        Assert.AreEqual(4, f.CurrentBar);
    }

    [Test]
    public void Follower_FollowsATransposedPlayer()
    {
        var f = new SheetFollower(Mary()) { Transpose = 2 }; // in D: F# E D E | F#
        foreach (int midi in new[] { 66, 64, 62, 64, 66 }) f.NoteOn(midi);
        Assert.AreEqual(2, f.CurrentBar);
    }

    [TestCase("show Jingle Bells", "jingle bells")]
    [TestCase("Show me the music for Für Elise.", "fur elise")]
    [TestCase("Can you pull up the sheet music for Happy Birthday please?", "happy birthday")]
    [TestCase("open twinkle twinkle", "twinkle twinkle")]
    public void ShowCommand_Recognized(string text, string song)
    {
        Assert.IsTrue(FreePlaySheet.TryParseShowCommand(text, out string asked));
        Assert.AreEqual(song, asked);
    }

    [TestCase("show the music")]
    [TestCase("What is the music for this?")]
    [TestCase("")]
    public void ShowCommand_NotACommand(string text)
    {
        Assert.IsFalse(FreePlaySheet.TryParseShowCommand(text, out _));
    }

    [TestCase("Close the music.")]
    [TestCase("hide the sheet music")]
    [TestCase("put away the sheet")]
    public void CloseCommand_Recognized(string text)
    {
        Assert.IsTrue(FreePlaySheet.IsCloseCommand(text));
    }

    static readonly SongEntry[] Songs =
    {
        new SongEntry { id = "fur_elise", name = "Für Elise" },
        new SongEntry { id = "happy_birthday", name = "Happy Birthday to You" },
        new SongEntry { id = "jingle_bells", name = "Jingle Bells" },
        new SongEntry { id = "chopin_minute_waltz_op64_no1", name = "Waltz in D-flat \"Minute Waltz\"" },
        new SongEntry { id = "frere_jacques", name = "Frère Jacques (Are You Sleeping?)" },
        new SongEntry { id = "twinkle_twinkle", name = "Twinkle, Twinkle, Little Star" },
    };

    [TestCase("fur elise", "fur_elise")]
    [TestCase("happy birthday", "happy_birthday")]
    [TestCase("jingle bell", "jingle_bells")]      // one letter off
    [TestCase("the minute waltz", "chopin_minute_waltz_op64_no1")]
    [TestCase("are you sleeping", "frere_jacques")]
    [TestCase("twinkle twinkle little star", "twinkle_twinkle")]
    public void FindSong_ByName(string asked, string id)
    {
        Assert.AreEqual(id, FreePlaySheet.FindSong(asked, Songs)?.id);
    }

    [TestCase("how to play bar three")]
    [TestCase("moonlight sonata")]
    public void FindSong_None(string asked)
    {
        Assert.IsNull(FreePlaySheet.FindSong(asked, Songs));
    }

    [Test]
    public void EverySongInTheDatabaseIsFoundByItsName()
    {
        var all = SongDatabase.All.ToList();
        Assert.Greater(all.Count, 0);
        foreach (var e in all) Assert.AreEqual(e.id, FreePlaySheet.FindSong(e.name, all)?.id, e.name);
    }
}
