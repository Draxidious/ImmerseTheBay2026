using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Turns a SongData into the compact text the coach model reads, for example:
//
//   Ode to Joy | 120 bpm | 4 beats per bar | 16 bars
//   RH | 1: E4 E4 F4 G4 | 2: G4 F4 E4 D4 | 3: C4 C4 D4 E4 | 4: E4:1.5 D4:0.5 D4:2
//   LH | 1: C3:4 | 2: G2:4
//
// A note is one beat long unless it has ":beats". Notes played together are joined with "+".
public static class SongFormatter
{
    static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    static readonly Dictionary<string, string> cache = new Dictionary<string, string>();

    public static string NoteName(int midi) => NoteNames[((midi % 12) + 12) % 12] + (midi / 12 - 1);

    // Cached per song id. Call Invalidate if a song's notes change after the first call.
    public static string Format(SongData song)
    {
        if (song == null) return "";
        string key = string.IsNullOrEmpty(song.id) ? song.name : song.id;
        if (!string.IsNullOrEmpty(key) && cache.TryGetValue(key, out string cached)) return cached;

        string text = Build(song);
        if (!string.IsNullOrEmpty(key)) cache[key] = text;
        return text;
    }

    public static void Invalidate(string songId = null)
    {
        if (songId == null) cache.Clear();
        else cache.Remove(songId);
    }

    static string Build(SongData song)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrEmpty(song.name) ? song.id : song.name)
          .Append(" | ").Append(song.tempoBpm).Append(" bpm")
          .Append(" | ").Append(song.beatsPerBar).Append(" beats per bar")
          .Append(" | ").Append(song.BarCount).Append(" bars");

        AppendHand(sb, song, PianoHand.Right, "RH");
        AppendHand(sb, song, PianoHand.Left, "LH");
        return sb.ToString();
    }

    static void AppendHand(StringBuilder sb, SongData song, PianoHand hand, string label)
    {
        var notes = song.notes.Where(n => n.hand == hand).OrderBy(n => n.bar).ThenBy(n => n.beat).ThenBy(n => n.midi).ToList();
        if (notes.Count == 0) return;

        sb.Append('\n').Append(label);
        int bar = -1;
        float beat = -1f;
        foreach (var n in notes)
        {
            if (n.bar != bar)
            {
                sb.Append(" | ").Append(n.bar).Append(": ");
                bar = n.bar;
            }
            else
            {
                sb.Append(n.beat == beat ? "+" : " "); // same beat = chord
            }
            beat = n.beat;

            sb.Append(NoteName(n.midi));
            if (n.beats != 1f) sb.Append(':').Append(n.beats.ToString("0.##", CultureInfo.InvariantCulture));
        }
    }
}
