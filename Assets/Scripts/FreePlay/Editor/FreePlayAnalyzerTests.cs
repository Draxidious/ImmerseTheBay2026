using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// EditMode tests for FreePlayAnalyzer (Window > General > Test Runner > EditMode). Song openings are hardcoded
// from Assets/Resources/SongDB so the tests don't depend on Resources.
public class FreePlayAnalyzerTests
{
    // Top voice of the right hand, first 16 notes, as SongTemplates.FromDatabase builds them.
    static readonly int[] FurElise = { 76, 75, 76, 75, 76, 71, 74, 72, 69, 60, 64, 69, 71, 64, 68, 71 };
    static readonly int[] OdeToJoy = { 64, 64, 65, 67, 67, 65, 64, 62, 60, 60, 62, 64, 64, 62, 62, 64 };
    static readonly int[] HotCrossBuns = { 64, 62, 60, 64, 62, 60, 60, 60, 60, 60, 62, 62, 62, 62, 64, 62 };
    static readonly int[] Twinkle = { 60, 60, 67, 67, 69, 69, 67, 65, 65, 64, 64, 62, 62, 60, 67, 67 };
    static readonly int[] Entertainer = { 62, 63, 64, 72, 64, 72, 64, 72, 72, 74, 75, 76, 72, 74, 76, 71 };
    static readonly int[] Chopsticks = { 67, 67, 67, 67, 67, 67, 67, 67, 67, 67, 67, 67, 71, 71, 71, 71 };

    static List<SongTemplate> Songs() => new List<SongTemplate>
    {
        new SongTemplate { id = "hot_cross_buns", name = "Hot Cross Buns", opening = HotCrossBuns },
        new SongTemplate { id = "ode_to_joy", name = "Ode to Joy", opening = OdeToJoy },
        new SongTemplate { id = "twinkle_twinkle", name = "Twinkle Twinkle Little Star", opening = Twinkle },
        new SongTemplate { id = "chopsticks", name = "Chopsticks", opening = Chopsticks },
        new SongTemplate { id = "the_entertainer", name = "The Entertainer", opening = Entertainer },
        new SongTemplate { id = "fur_elise", name = "Für Elise", opening = FurElise },
    };

    static string Chord(params int[] midis) => FreePlayAnalyzer.IdentifyChord(midis)?.name;

    // ---- IdentifyChord ----

    [Test] public void Chord_CMajorRoot() { Assert.AreEqual("C", Chord(60, 64, 67)); Assert.AreEqual(0, FreePlayAnalyzer.IdentifyChord(new[] { 60, 64, 67 }).inversion); }
    [Test] public void Chord_CMajorFirstInversion() { var c = FreePlayAnalyzer.IdentifyChord(new[] { 64, 67, 72 }); Assert.AreEqual("C/E", c.name); Assert.AreEqual(1, c.inversion); Assert.AreEqual("major", c.quality); }
    [Test] public void Chord_CMajorSecondInversion() { var c = FreePlayAnalyzer.IdentifyChord(new[] { 67, 72, 76 }); Assert.AreEqual("C/G", c.name); Assert.AreEqual(2, c.inversion); }
    [Test] public void Chord_AMinorAndInversions()
    {
        Assert.AreEqual("Am", Chord(57, 60, 64));
        Assert.AreEqual("Am/C", Chord(60, 64, 69));
        Assert.AreEqual("Am/E", Chord(64, 69, 72));
        Assert.AreEqual("minor", FreePlayAnalyzer.IdentifyChord(new[] { 57, 60, 64 }).quality);
    }
    [Test] public void Chord_BDiminished() { Assert.AreEqual("Bdim", Chord(59, 62, 65)); }
    [Test] public void Chord_CAugmented() { Assert.AreEqual("Caug", Chord(60, 64, 68)); }
    [Test] public void Chord_Dsus4() { Assert.AreEqual("Dsus4", Chord(62, 67, 69)); }
    [Test] public void Chord_Dsus2() { Assert.AreEqual("Dsus2", Chord(62, 64, 69)); }
    [Test] public void Chord_G7AndG7OverB()
    {
        Assert.AreEqual("G7", Chord(55, 59, 62, 65));
        var c = FreePlayAnalyzer.IdentifyChord(new[] { 59, 62, 65, 67 });
        Assert.AreEqual("G7/B", c.name);
        Assert.AreEqual(1, c.inversion);
        Assert.AreEqual("dominant 7th", c.quality);
    }
    [Test] public void Chord_Fmaj7() { Assert.AreEqual("Fmaj7", Chord(53, 57, 60, 64)); }
    [Test] public void Chord_Em7() { Assert.AreEqual("Em7", Chord(52, 55, 59, 62)); }
    [Test] public void Chord_Bm7b5() { Assert.AreEqual("Bm7b5", Chord(59, 62, 65, 69)); }
    [Test] public void Chord_Bdim7() { Assert.AreEqual("Bdim7", Chord(59, 62, 65, 68)); }
    [Test] public void Chord_DoubledNotes() { Assert.AreEqual("C", Chord(48, 60, 64, 67)); }
    [Test] public void Chord_AmbiguousNamedByBass() { Assert.AreEqual("C6", Chord(60, 64, 67, 69)); Assert.AreEqual("Am7", Chord(57, 60, 64, 67)); }
    [Test] public void Chord_TwoNotesIsNull() { Assert.IsNull(Chord(60, 64)); Assert.IsNull(Chord(60, 64, 72)); }
    [Test] public void Chord_ClusterIsNull() { Assert.IsNull(Chord(60, 61, 62)); }

    // ---- EstimateKey ----

    static List<FreePlayNote> Blocks(params int[][] chords)
    {
        var notes = new List<FreePlayNote>();
        for (int i = 0; i < chords.Length; i++)
            foreach (int m in chords[i]) notes.Add(new FreePlayNote { midi = m, velocity = 80, onset = i, release = i + 1.0 });
        return notes;
    }

    [Test] public void Key_CFGC_IsCMajor()
    {
        var k = FreePlayAnalyzer.EstimateKey(Blocks(new[] { 60, 64, 67 }, new[] { 65, 69, 72 }, new[] { 67, 71, 74 }, new[] { 60, 64, 67 }), new FreePlaySettings());
        Assert.AreEqual("C major", k.name);
        Assert.AreEqual(0, k.tonic);
        Assert.IsTrue(k.major);
        Assert.IsTrue(k.IsClear, $"r={k.correlation} margin={k.margin}");
    }

    [Test] public void Key_AmDmEAm_IsAMinor()
    {
        var k = FreePlayAnalyzer.EstimateKey(Blocks(new[] { 57, 60, 64 }, new[] { 62, 65, 69 }, new[] { 64, 68, 71 }, new[] { 57, 60, 64 }), new FreePlaySettings());
        Assert.AreEqual("A minor", k.name);
        Assert.IsFalse(k.major);
        Assert.IsTrue(k.IsClear, $"r={k.correlation} margin={k.margin}");
    }

    [Test] public void Key_RepeatedNoteNotClear()
    {
        var k = FreePlayAnalyzer.EstimateKey(Blocks(new[] { 60 }, new[] { 60 }, new[] { 60 }, new[] { 60 }, new[] { 60 }), new FreePlaySettings());
        Assert.IsFalse(k.IsClear);
    }

    [Test] public void Key_HeldNoteCapped()
    {
        // A C major arpeggio over a 30 s held F#: the F# counts as 2 s at most, so it's still C major.
        var notes = Blocks(new[] { 60, 64, 67 }, new[] { 65, 69, 72 }, new[] { 67, 71, 74 }, new[] { 60, 64, 67 });
        notes.Add(new FreePlayNote { midi = 42, velocity = 80, onset = 0, release = 30 });
        Assert.AreEqual("C major", FreePlayAnalyzer.EstimateKey(notes, new FreePlaySettings()).name);
    }

    [Test] public void Key_EmptyIsNull() { Assert.IsNull(FreePlayAnalyzer.EstimateKey(new List<FreePlayNote>(), new FreePlaySettings())); }

    // ---- RomanNumeral ----

    [Test] public void Roman_MajorAndMinorKeys()
    {
        var c = new KeyEstimate { tonic = 0, major = true, name = "C major", IsClear = true };
        var a = new KeyEstimate { tonic = 9, major = false, name = "A minor", IsClear = true };
        string R(KeyEstimate k, params int[] m) => FreePlayAnalyzer.RomanNumeral(FreePlayAnalyzer.IdentifyChord(m), k);
        Assert.AreEqual("I", R(c, 60, 64, 67));
        Assert.AreEqual("ii", R(c, 62, 65, 69));
        Assert.AreEqual("vi", R(c, 57, 60, 64));
        Assert.AreEqual("V7", R(c, 55, 59, 62, 65));
        Assert.AreEqual("vii°", R(c, 59, 62, 65));
        Assert.AreEqual("IVmaj7", R(c, 53, 57, 60, 64));
        Assert.IsNull(R(c, 61, 65, 68)); // C# major in C major
        Assert.AreEqual("i", R(a, 57, 60, 64));
        Assert.AreEqual("iv", R(a, 62, 65, 69));
        Assert.AreEqual("V", R(a, 64, 68, 71));
        Assert.AreEqual("v", R(a, 64, 67, 71));
        Assert.AreEqual("III", R(a, 60, 64, 67));
        Assert.AreEqual("VI", R(a, 65, 69, 72));
        Assert.AreEqual("VII", R(a, 67, 71, 74));
        Assert.AreEqual("ii°", R(a, 59, 62, 65));
        Assert.AreEqual("vii°", R(a, 68, 71, 74));
    }

    // ---- RateSteadiness ----

    [Test] public void Steadiness_Even()
    {
        var s = FreePlayAnalyzer.RateSteadiness(new List<double> { 0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 }, out float cv);
        Assert.AreEqual(Steadiness.Steady, s);
        Assert.Less(cv, 0.01f);
    }

    [Test] public void Steadiness_Jittered()
    {
        Assert.AreEqual(Steadiness.Uneven, FreePlayAnalyzer.RateSteadiness(new List<double> { 0, 0.3, 1.1, 1.3, 2.4, 2.6, 3.5 }, out _));
        Assert.AreEqual(Steadiness.ABitUneven, FreePlayAnalyzer.RateSteadiness(new List<double> { 0, 0.5, 1.1, 1.5, 2.1, 2.5 }, out _));
    }

    [Test] public void Steadiness_TooFew() { Assert.AreEqual(Steadiness.NotEnoughNotes, FreePlayAnalyzer.RateSteadiness(new List<double> { 0, 0.5, 1, 1.5 }, out _)); }

    // ---- MatchSong ----

    static SongMatch Match(params int[] melody) => FreePlayAnalyzer.MatchSong(melody, Songs(), new FreePlaySettings().songMatchMinNotes);

    [Test] public void Song_FurElise()
    {
        var m = Match(76, 75, 76, 75, 76, 71, 74, 72, 69);
        Assert.IsNotNull(m);
        Assert.AreEqual("fur_elise", m.songId);
        Assert.AreEqual(9, m.matchedNotes);
        Assert.AreEqual(0, m.transpose);
    }

    [Test] public void Song_FurEliseTransposed()
    {
        var m = Match(71, 70, 71, 70, 71, 66, 69, 67, 64);
        Assert.AreEqual("fur_elise", m?.songId);
        Assert.AreEqual(-5, m.transpose);
    }

    [Test] public void Song_FurEliseAfterNoodling()
    {
        var m = Match(60, 62, 55, 67, 64, 76, 75, 76, 75, 76, 71, 74, 72, 69);
        Assert.AreEqual("fur_elise", m?.songId);
        Assert.AreEqual(9, m.matchedNotes);
    }

    [Test] public void Song_OdeToJoy()
    {
        var m = Match(64, 64, 65, 67, 67, 65, 64, 62, 60, 60, 62, 64, 64, 62, 62);
        Assert.AreEqual("ode_to_joy", m?.songId);
        Assert.AreEqual(15, m.matchedNotes);
    }

    [Test] public void Song_OdeToJoyTooShortToBeDistinctive() { Assert.IsNull(Match(64, 64, 65, 67, 67, 65)); }
    [Test] public void Song_HotCrossBuns() { Assert.AreEqual("hot_cross_buns", Match(64, 62, 60, 64, 62, 60)?.songId); }
    [Test] public void Song_TooFewNotes() { Assert.IsNull(Match(76, 75, 76, 75, 76)); }

    [Test] public void Song_FalseMatchGuards()
    {
        Assert.IsNull(Match(60, 62, 64, 65, 67, 69, 71, 72), "scale up");
        Assert.IsNull(Match(72, 71, 69, 67, 65, 64, 62, 60), "scale down");
        Assert.IsNull(Match(60, 62, 64, 65, 67, 69, 71, 72, 71, 69, 67, 65, 64, 62, 60), "up and down");
        Assert.IsNull(Match(60, 62, 64, 62, 60), "C-D-E-D-C");
        Assert.IsNull(Match(60, 62, 64, 62, 60, 62, 64, 62, 60), "C-D-E-D-C twice");
        Assert.IsNull(Match(60, 60, 60, 60, 60, 60, 60, 60), "repeated C");
        Assert.IsNull(Match(67, 67, 67, 67, 67, 67, 67, 67, 67, 67, 67, 67), "repeated G (chopsticks top voice)");
    }

    [Test] public void Song_Distinctiveness()
    {
        Assert.IsTrue(FreePlayAnalyzer.IsDistinctive(new[] { -1, 1, -5 }));
        Assert.IsFalse(FreePlayAnalyzer.IsDistinctive(new[] { 2, 2, 1, 2, 2 }));
        Assert.IsFalse(FreePlayAnalyzer.IsDistinctive(new[] { 0, 1, 2, 0, -2 }));          // 6 notes
        Assert.IsTrue(FreePlayAnalyzer.IsDistinctive(new[] { 0, 1, 2, 0, -2, -1, -2 }));   // 8 notes
    }

    // ---- Analyzer end to end ----

    static void PlayChord(FreePlayAnalyzer fp, double t, double len, params int[] midis)
    {
        foreach (int m in midis) fp.NoteOn(m, 80, t);
        foreach (int m in midis) fp.NoteOff(m, t + len);
    }

    [Test] public void Analyzer_CFGC()
    {
        var fp = new FreePlayAnalyzer(new FreePlaySettings(), Songs());
        Assert.IsNull(fp.Peek(0));
        PlayChord(fp, 0, 0.9, 60, 64, 67);
        PlayChord(fp, 1, 0.9, 65, 69, 72);
        PlayChord(fp, 2, 0.9, 67, 71, 74);
        PlayChord(fp, 3, 0.9, 60, 64, 67);
        Assert.IsTrue(fp.PhraseInProgress);
        Assert.IsNull(fp.Poll(4.0));
        Assert.IsNull(fp.Poll(5.8));

        var a = fp.Poll(5.95);
        Assert.IsNotNull(a);
        CollectionAssert.AreEqual(new[] { "C", "F", "G", "C" }, a.chords.Select(c => c.name).ToArray());
        Assert.IsNotNull(a.romanNumerals);
        CollectionAssert.AreEqual(new[] { "I", "IV", "V", "I" }, a.romanNumerals.ToArray());
        Assert.AreEqual("C major", a.key.name);
        Assert.IsTrue(a.key.IsClear);
        Assert.AreEqual(12, a.noteCount);
        Assert.AreEqual(60, a.lowest);
        Assert.AreEqual(74, a.highest);
        Assert.AreEqual(0, a.start, 1e-9);
        Assert.AreEqual(3.9, a.end, 1e-9);
        Assert.AreEqual("right", a.hands);
        CollectionAssert.AreEqual(new[] { 67, 72, 74, 67 }, a.melody.ToArray());
        Assert.AreEqual(Steadiness.NotEnoughNotes, a.steadiness);
        Assert.IsNull(a.song);

        Assert.IsFalse(fp.PhraseInProgress);
        Assert.AreEqual(1, fp.Session.phrases);
        Assert.AreEqual(12, fp.Session.notes);
        Assert.AreEqual(60, fp.Session.lowest);
        Assert.AreEqual(74, fp.Session.highest);
        CollectionAssert.AreEqual(new[] { "C", "F", "G" }, fp.Session.chordsSeen.ToArray());
        Assert.AreEqual("C major", fp.Session.rollingKey?.name);

        Assert.IsNull(fp.Poll(7.0));
        Assert.AreSame(a, fp.Peek(7.0));
    }

    [Test] public void Analyzer_HeldKeyKeepsPhraseOpen()
    {
        var fp = new FreePlayAnalyzer(new FreePlaySettings(), Songs());
        fp.NoteOn(48, 80, 0);
        PlayChord(fp, 0.5, 0.4, 64);
        Assert.IsNull(fp.Poll(5.0)); // C3 still held
        fp.NoteOff(48, 5.0);
        Assert.IsNull(fp.Poll(6.0));
        Assert.IsNotNull(fp.Poll(7.0));
    }

    [Test] public void Analyzer_PeekDuringPhrase()
    {
        var fp = new FreePlayAnalyzer(new FreePlaySettings(), Songs());
        PlayChord(fp, 0, 0.9, 60, 64, 67);
        foreach (int m in new[] { 65, 69, 72 }) fp.NoteOn(m, 80, 1.0); // F chord still held

        var p = fp.Peek(1.5);
        Assert.IsNotNull(p);
        Assert.AreEqual(6, p.noteCount);
        CollectionAssert.AreEqual(new[] { "C", "F" }, p.chords.Select(c => c.name).ToArray());
        Assert.IsTrue(fp.PhraseInProgress);
        Assert.AreEqual(0, fp.Session.phrases);
        Assert.IsNull(fp.Poll(10.0)); // keys still held
    }

    [Test] public void Analyzer_RecognizesFurEliseWithChordsCollapsed()
    {
        var fp = new FreePlayAnalyzer(new FreePlaySettings(), Songs());
        double t = 0;
        foreach (int m in new[] { 76, 75, 76, 75, 76, 71, 74, 72, 69 }) { PlayChord(fp, t, 0.2, m); t += 0.25; }
        PlayChord(fp, t, 0.4, 57, 60, 64); t += 0.5;
        PlayChord(fp, t, 0.4, 57, 60, 64);
        var a = fp.Poll(t + 3);
        Assert.AreEqual("fur_elise", a.song?.songId);
        Assert.AreEqual(1, a.chords.Count);
        Assert.AreEqual("Am", a.chords[0].name);
        CollectionAssert.AreEqual(new[] { "fur_elise" }, fp.Session.songsRecognized.ToArray());
    }

    [Test] public void Analyzer_BothHands()
    {
        var fp = new FreePlayAnalyzer(new FreePlaySettings(), Songs());
        PlayChord(fp, 0, 0.4, 45, 76);
        PlayChord(fp, 0.5, 0.4, 52, 75);
        PlayChord(fp, 1.0, 0.4, 57, 76);
        Assert.AreEqual("both", fp.Poll(5).hands);
    }

    [Test] public void Analyzer_Reset()
    {
        var fp = new FreePlayAnalyzer(new FreePlaySettings(), Songs());
        PlayChord(fp, 0, 0.9, 60, 64, 67);
        fp.Poll(5);
        fp.Reset();
        Assert.AreEqual(0, fp.Session.phrases);
        Assert.IsNull(fp.Peek(6));
        Assert.IsFalse(fp.PhraseInProgress);
    }
}
