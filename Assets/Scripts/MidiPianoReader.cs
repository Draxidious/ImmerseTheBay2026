using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// Reads MIDI from a USB keyboard (e.g. Yamaha P-71) via the Windows winmm API and logs
/// notes, pedal and detected chords to the Unity console. Windows only, no packages needed.
/// Add to any GameObject and press Play.
/// </summary>
public class MidiPianoReader : MonoBehaviour
{
    [Tooltip("Substring of the MIDI device name to connect to. Leave empty to use the first device.")]
    public string deviceNameContains = "";
    public bool logNotes = true;
    public bool logChords = true;
    public bool logPedalAndOther = true;
    [Tooltip("Seconds to wait after a note-on before naming the chord, so a rolled chord is grouped.")]
    public float chordWindow = 0.08f;

    // Public state other scripts can read later.
    public readonly HashSet<int> HeldNotes = new HashSet<int>();
    public event Action<int, int> NoteOn;   // note, velocity
    public event Action<int> NoteOff;       // note

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    private delegate void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MIDIINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Ansi)] private static extern uint midiInGetDevCaps(UIntPtr uDeviceID, ref MIDIINCAPS caps, uint cbMidiInCaps);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out IntPtr hMidiIn, uint uDeviceID, MidiInProc dwCallback, IntPtr dwInstance, uint dwFlags);
    [DllImport("winmm.dll")] private static extern uint midiInStart(IntPtr hMidiIn);
    [DllImport("winmm.dll")] private static extern uint midiInStop(IntPtr hMidiIn);
    [DllImport("winmm.dll")] private static extern uint midiInReset(IntPtr hMidiIn);
    [DllImport("winmm.dll")] private static extern uint midiInClose(IntPtr hMidiIn);

    private const uint CALLBACK_FUNCTION = 0x00030000;
    private const uint MIM_DATA = 0x3C3;

    private IntPtr _handle = IntPtr.Zero;
    private MidiInProc _callback; // keep a reference so the GC doesn't collect it
    private readonly ConcurrentQueue<uint> _queue = new ConcurrentQueue<uint>();

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private float _chordTimer = -1f;
    private string _lastChord = "";

    private void OnEnable()
    {
        uint count = midiInGetNumDevs();
        if (count == 0)
        {
            Debug.LogWarning("[MIDI] No MIDI input devices found. Is the piano plugged in and powered on?");
            return;
        }

        var names = new List<string>();
        int chosen = -1;
        for (uint i = 0; i < count; i++)
        {
            var caps = new MIDIINCAPS();
            midiInGetDevCaps((UIntPtr)i, ref caps, (uint)Marshal.SizeOf(typeof(MIDIINCAPS)));
            names.Add(caps.szPname);
            if (chosen < 0 && (string.IsNullOrEmpty(deviceNameContains) ||
                caps.szPname.IndexOf(deviceNameContains, StringComparison.OrdinalIgnoreCase) >= 0))
                chosen = (int)i;
        }
        Debug.Log("[MIDI] Input devices: " + string.Join(" | ", names));

        if (chosen < 0)
        {
            Debug.LogWarning("[MIDI] No device matched '" + deviceNameContains + "'.");
            return;
        }

        _callback = OnMidi;
        uint r = midiInOpen(out _handle, (uint)chosen, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
        if (r != 0)
        {
            Debug.LogError("[MIDI] midiInOpen failed (code " + r + "). Another app may be using the device.");
            _handle = IntPtr.Zero;
            return;
        }
        midiInStart(_handle);
        Debug.Log("[MIDI] Listening on: " + names[chosen]);
    }

    private void OnDisable()
    {
        if (_handle != IntPtr.Zero)
        {
            midiInStop(_handle);
            midiInReset(_handle);
            midiInClose(_handle);
            _handle = IntPtr.Zero;
        }
        HeldNotes.Clear();
    }

    // Runs on a winmm thread: only enqueue, never touch Unity API here.
    private void OnMidi(IntPtr hMidiIn, uint msg, IntPtr instance, IntPtr p1, IntPtr p2)
    {
        if (msg == MIM_DATA) _queue.Enqueue((uint)p1.ToInt64());
    }

    private void Update()
    {
        while (_queue.TryDequeue(out uint data))
            Handle((int)(data & 0xFF), (int)((data >> 8) & 0xFF), (int)((data >> 16) & 0xFF));

        if (_chordTimer >= 0f && Time.unscaledTime >= _chordTimer)
        {
            _chordTimer = -1f;
            AnnounceChord();
        }
    }

    private void Handle(int status, int d1, int d2)
    {
        if (status >= 0xF8) return; // ignore realtime/clock/active-sensing
        int type = status & 0xF0;
        int channel = (status & 0x0F) + 1;

        switch (type)
        {
            case 0x90 when d2 > 0:
                HeldNotes.Add(d1);
                if (logNotes) Debug.Log($"[MIDI] Note ON  {NoteName(d1)} (#{d1}) vel {d2} ch {channel}");
                NoteOn?.Invoke(d1, d2);
                _chordTimer = Time.unscaledTime + chordWindow;
                break;
            case 0x80:
            case 0x90: // note-on with velocity 0 == note-off
                HeldNotes.Remove(d1);
                if (logNotes) Debug.Log($"[MIDI] Note OFF {NoteName(d1)} (#{d1}) ch {channel}");
                NoteOff?.Invoke(d1);
                if (HeldNotes.Count == 0) _lastChord = "";
                break;
            case 0xB0:
                if (logPedalAndOther)
                {
                    string label = d1 == 64 ? "Sustain pedal" : "CC " + d1;
                    Debug.Log($"[MIDI] {label}: {d2}" + (d1 == 64 ? (d2 >= 64 ? " (down)" : " (up)") : ""));
                }
                break;
            default:
                if (logPedalAndOther) Debug.Log($"[MIDI] status 0x{status:X2} data {d1},{d2}");
                break;
        }
    }

    private void AnnounceChord()
    {
        if (!logChords || HeldNotes.Count < 2) return;
        var sorted = HeldNotes.OrderBy(n => n).ToList();
        string notes = string.Join(" ", sorted.Select(NoteName));
        string name = IdentifyChord(sorted);
        string line = name != null ? $"{name}  [{notes}]" : $"[{notes}]";
        if (line == _lastChord) return;
        _lastChord = line;
        Debug.Log("[MIDI] Chord: " + line);
    }

    private static readonly (string suffix, int[] intervals)[] ChordTypes =
    {
        ("", new[] {0, 4, 7}), ("m", new[] {0, 3, 7}), ("dim", new[] {0, 3, 6}), ("aug", new[] {0, 4, 8}),
        ("sus2", new[] {0, 2, 7}), ("sus4", new[] {0, 5, 7}),
        ("6", new[] {0, 4, 7, 9}), ("m6", new[] {0, 3, 7, 9}),
        ("7", new[] {0, 4, 7, 10}), ("maj7", new[] {0, 4, 7, 11}), ("m7", new[] {0, 3, 7, 10}),
        ("mMaj7", new[] {0, 3, 7, 11}), ("dim7", new[] {0, 3, 6, 9}), ("m7b5", new[] {0, 3, 6, 10}),
        ("7sus4", new[] {0, 5, 7, 10}), ("add9", new[] {0, 2, 4, 7}),
        ("9", new[] {0, 2, 4, 7, 10}), ("maj9", new[] {0, 2, 4, 7, 11}), ("m9", new[] {0, 2, 3, 7, 10}),
        ("5", new[] {0, 7}),
    };

    private static string IdentifyChord(List<int> sortedNotes)
    {
        var pcs = new HashSet<int>(sortedNotes.Select(n => n % 12));
        int bass = sortedNotes[0] % 12;
        string best = null;
        int bestScore = int.MaxValue;

        foreach (int root in pcs)
        {
            var rel = new HashSet<int>(pcs.Select(p => (p - root + 12) % 12));
            foreach (var (suffix, intervals) in ChordTypes)
            {
                if (!rel.SetEquals(intervals)) continue;
                // Prefer root == bass note, then simpler chords (earlier in list).
                int score = (root == bass ? 0 : 100) + Array.FindIndex(ChordTypes, c => c.suffix == suffix);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = NoteNames[root] + suffix + (root != bass ? "/" + NoteNames[bass] : "");
                }
            }
        }
        return best;
    }

    private static string NoteName(int note) => NoteNames[note % 12] + (note / 12 - 1);
#else
    private void OnEnable()
    {
        Debug.LogWarning("[MIDI] MidiPianoReader currently supports Windows only.");
    }
#endif
}
