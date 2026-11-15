using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Minimal Standard MIDI File reader (format 0 and 1) + conversion to a <see cref="SongEntry"/> with explicit notes.
/// Plain C#, no dependencies (no DryWetMIDI), so it works in the Editor and on Android/IL2CPP.
/// Used by the Editor menu Tools ▸ Songs ▸ Import MIDI files.
/// </summary>
public static class MidiSongImporter
{
    public class MidiNote
    {
        public int track, channel, midi, velocity;
        public long startTick, endTick;
    }

    public class MidiFile
    {
        public int format, ticksPerQuarter;
        public double tempoQuarterBpm = 120;  // first tempo event
        public int timeSigNumerator = 4, timeSigDenominator = 4; // first time signature
        public int keyFifths; public bool keyMinor; public bool keySet; // first key signature (meta 0x59)
        public List<MidiNote> notes = new List<MidiNote>();
        public List<string> trackNames = new List<string>();
    }

    public class Options
    {
        public bool simplify;          // keep only the top note (RH melody) and bottom note (LH bass) at each moment
        public int splitNote = 60;     // one-track files: notes >= this go to the right hand
        public float grid = 1f / 24f;  // quantize to this many beats (1/24 keeps 16ths, 32nds and triplets/sextuplets exact)
        public int maxBars;            // 0 = all
        public bool detectPickup = true; // find a pickup (anacrusis): MIDI files start at the pickup, not at bar 1
        public double pickupQuarters = -1; // known pickup length in quarter notes (e.g. from the score's \partial); >= 0 skips detection
    }

    /// <summary>
    /// Ticks before the first full bar (0 = no pickup). Engraved MIDI (e.g. LilyPond/Mutopia) starts at the pickup, so the
    /// real bar lines can be offset from tick 0. Picks the offset (in 16ths) that puts the most weight on downbeats:
    /// long notes and the lowest track (the bass usually lands on the beat). Ties prefer no pickup.
    /// </summary>
    public static long DetectPickupTicks(MidiFile f, double ticksPerBar)
    {
        if (f.notes.Count == 0 || ticksPerBar <= 0) return 0;
        int bassTrack = f.notes.GroupBy(n => n.track).OrderBy(g => g.Average(n => n.midi)).First().Key;
        double step = f.ticksPerQuarter / 4.0, tolerance = f.ticksPerQuarter / 32.0;
        long firstStart = f.notes.Min(n => n.startTick);
        double Score(double offset)
        {
            double s = 0;
            foreach (var n in f.notes)
            {
                double phase = ((n.startTick - offset) % ticksPerBar + ticksPerBar) % ticksPerBar;
                if (phase <= tolerance || ticksPerBar - phase <= tolerance)
                    s += (n.endTick - n.startTick) * (n.track == bassTrack ? 2.0 : 1.0);
            }
            return s;
        }
        double baseScore = Score(0), best = 0, bestScore = baseScore;
        for (double offset = step; offset < ticksPerBar - 1e-6; offset += step)
        {
            if (offset > firstStart + ticksPerBar) break;
            double sc = Score(offset);
            if (sc > bestScore) { bestScore = sc; best = offset; }
        }
        return bestScore > baseScore * 1.25 ? (long)Math.Round(best) : 0; // clearly better, or no pickup
    }

    // ---------- Reading ----------

    public static MidiFile Read(byte[] data)
    {
        var f = new MidiFile();
        int pos = 0;
        if (ReadAscii(data, ref pos, 4) != "MThd") throw new FormatException("Not a MIDI file (no MThd header).");
        int headerLen = (int)ReadU32(data, ref pos);
        int headerEnd = pos + headerLen;
        f.format = ReadU16(data, ref pos);
        int tracks = ReadU16(data, ref pos);
        int division = ReadU16(data, ref pos);
        if ((division & 0x8000) != 0) throw new FormatException("SMPTE time division isn't supported.");
        f.ticksPerQuarter = division;
        pos = headerEnd;

        bool tempoSet = false, sigSet = false;
        for (int t = 0; t < tracks && pos < data.Length; t++)
        {
            string id = ReadAscii(data, ref pos, 4);
            int len = (int)ReadU32(data, ref pos);
            int end = Math.Min(data.Length, pos + len);
            if (id != "MTrk") { pos = end; continue; }

            long tick = 0;
            int running = 0;
            string trackName = "";
            var open = new Dictionary<(int ch, int key), Stack<MidiNote>>();
            while (pos < end)
            {
                tick += ReadVlq(data, ref pos);
                int status = data[pos];
                if (status < 0x80) status = running; else pos++;
                if (status < 0x80) throw new FormatException("Running status without a previous status byte.");

                if (status == 0xFF)
                {
                    int type = data[pos++];
                    int mlen = (int)ReadVlq(data, ref pos);
                    if (type == 0x51 && mlen == 3 && !tempoSet)
                    {
                        int usPerQuarter = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];
                        if (usPerQuarter > 0) { f.tempoQuarterBpm = 60_000_000.0 / usPerQuarter; tempoSet = true; }
                    }
                    else if (type == 0x58 && mlen >= 2 && !sigSet)
                    {
                        f.timeSigNumerator = data[pos];
                        f.timeSigDenominator = 1 << data[pos + 1];
                        sigSet = true;
                    }
                    else if (type == 0x59 && mlen >= 2 && !f.keySet)
                    {
                        f.keyFifths = (sbyte)data[pos]; f.keyMinor = data[pos + 1] == 1; f.keySet = true;
                    }
                    else if (type == 0x03) trackName = System.Text.Encoding.ASCII.GetString(data, pos, mlen);
                    pos += mlen;
                    if (type == 0x2F) break;
                    continue;
                }
                if (status == 0xF0 || status == 0xF7) { pos += (int)ReadVlq(data, ref pos); continue; }

                running = status;
                int kind = status & 0xF0, ch = status & 0x0F;
                int d1 = data[pos++];
                int d2 = (kind == 0xC0 || kind == 0xD0) ? 0 : data[pos++];

                if (kind == 0x90 && d2 > 0)
                {
                    var n = new MidiNote { track = t, channel = ch, midi = d1, velocity = d2, startTick = tick, endTick = -1 };
                    if (!open.TryGetValue((ch, d1), out var st)) open[(ch, d1)] = st = new Stack<MidiNote>();
                    st.Push(n);
                    f.notes.Add(n);
                }
                else if (kind == 0x80 || (kind == 0x90 && d2 == 0))
                {
                    if (open.TryGetValue((ch, d1), out var st) && st.Count > 0) st.Pop().endTick = tick;
                }
            }
            foreach (var st in open.Values) foreach (var n in st) n.endTick = tick; // never released: end of track
            f.trackNames.Add(trackName);
            pos = end;
        }
        f.notes.RemoveAll(n => n.endTick <= n.startTick);
        return f;
    }

    // ---------- Converting ----------

    public static SongEntry ToEntry(MidiFile f, string id, string name, Options o)
    {
        int num = Math.Max(1, f.timeSigNumerator), den = Math.Max(1, f.timeSigDenominator);
        double ticksPerBeat = f.ticksPerQuarter * 4.0 / den; // a beat = the time signature's denominator note
        double ticksPerBar = ticksPerBeat * num;

        var e = new SongEntry
        {
            id = id,
            name = name,
            beatsPerBar = num,
            beatUnit = den == 4 ? "quarter" : den == 8 ? "eighth" : den == 2 ? "half" : $"1/{den}",
            tempoBpm = (int)Math.Round(f.tempoQuarterBpm * den / 4.0),
            arrangement = o.simplify ? "simplified: melody + bass" : "as written",
            keyFifths = f.keyFifths,
            keyMode = f.keyMinor ? "minor" : "major",
            verified = false,
        };

        // Hands: with 2+ note tracks the highest-sounding track is the right hand, the others the left;
        // with one track, split at o.splitNote.
        var tracks = f.notes.GroupBy(n => n.track).Select(g => (track: g.Key, avg: g.Average(n => n.midi))).ToList();
        int rhTrack = tracks.Count >= 2 ? tracks.OrderByDescending(x => x.avg).First().track : -1;
        bool IsRight(MidiNote n) => rhTrack >= 0 ? n.track == rhTrack : n.midi >= o.splitNote;

        double Quantize(double beats) => o.grid > 0 ? Math.Round(beats / o.grid) * o.grid : beats;
        long pickupTicks = o.pickupQuarters >= 0 ? (long)Math.Round(o.pickupQuarters * f.ticksPerQuarter)
                         : o.detectPickup ? DetectPickupTicks(f, ticksPerBar) : 0;
        double pickupBeats = Quantize(pickupTicks / ticksPerBeat);

        var raw = new List<SongNoteDef>();
        foreach (var n in f.notes)
        {
            // Beats from bar 1's downbeat; the pickup is negative and stored in bar 1 with beat < 1 (song DB convention).
            double startBeats = Quantize(n.startTick / ticksPerBeat) - pickupBeats;
            double lenBeats = Math.Max(o.grid > 0 ? o.grid : 0.125, Quantize((n.endTick - n.startTick) / ticksPerBeat));
            int bar = startBeats < 0 ? 1 : (int)Math.Floor(startBeats / num + 1e-9) + 1;
            if (o.maxBars > 0 && bar > o.maxBars) continue;
            raw.Add(new SongNoteDef
            {
                midi = n.midi,
                bar = bar,
                beat = (float)(startBeats - (bar - 1) * num + 1),
                beats = (float)lenBeats,
                hand = IsRight(n) ? "R" : "L",
            });
        }

        if (o.simplify)
        {
            // RH: top note at each onset (the melody). LH: bottom note at each onset (the bass).
            raw = raw.GroupBy(n => (n.hand, n.bar, n.beat))
                     .Select(g => g.Key.hand == "R" ? g.OrderByDescending(n => n.midi).First() : g.OrderBy(n => n.midi).First())
                     .ToList();
        }

        e.notes = raw.OrderBy(n => n.bar).ThenBy(n => n.beat).ThenBy(n => n.hand).ThenBy(n => n.midi).ToList();
        int bars = e.notes.Count == 0 ? 0 : e.notes.Max(n => n.bar);
        if (bars > 0) e.sections.Add(new SongSection { name = $"Bars 1–{bars}", firstBar = 1, lastBar = bars });
        return e;
    }

    public static SongEntry Import(string path, string id, string name, Options o) => ToEntry(Read(File.ReadAllBytes(path)), id, name, o);

    // ---------- Binary helpers ----------

    static string ReadAscii(byte[] d, ref int p, int n) { string s = System.Text.Encoding.ASCII.GetString(d, p, n); p += n; return s; }
    static int ReadU16(byte[] d, ref int p) { int v = (d[p] << 8) | d[p + 1]; p += 2; return v; }
    static uint ReadU32(byte[] d, ref int p) { uint v = (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]); p += 4; return v; }
    static long ReadVlq(byte[] d, ref int p)
    {
        long v = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = d[p++];
            v = (v << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }
}
