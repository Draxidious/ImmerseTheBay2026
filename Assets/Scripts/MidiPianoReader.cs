using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Reads MIDI from a USB keyboard (e.g. Yamaha P-71) and logs notes, pedal and chords.
///  - Windows (Editor/standalone): winmm.dll
///  - Android / Android XR (XREAL Aura puck): android.media.midi through Assets/Plugins/Android/MidiBridge.java
/// Other scripts (e.g. MidiLogUI) can subscribe to the events below.
/// </summary>
public class MidiPianoReader : MonoBehaviour
{
    [Tooltip("Windows only: substring of the MIDI device name to connect to. Empty = first device.")]
    public string deviceNameContains = "";
    public bool logNotes = true;
    public bool logChords = true;
    public bool logPedalAndOther = true;
    [Tooltip("Seconds to wait after a note-on before naming the chord, so a rolled chord is grouped.")]
    public float chordWindow = 0.08f;

    public readonly HashSet<int> HeldNotes = new HashSet<int>();
    public event Action<int, int> NoteOn;     // note, velocity
    public event Action<int> NoteOff;         // note
    public event Action<string> ChordChanged; // "Am7/C [C3 E3 G3 A3]", or "" when released
    public event Action<string> LogMessage;   // every line that is also sent to Debug.Log

    private readonly ConcurrentQueue<uint> _queue = new ConcurrentQueue<uint>();
    private readonly ConcurrentQueue<string> _statusQueue = new ConcurrentQueue<string>();
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private float _chordTimer = -1f;
    private string _lastChord = "";

    /// <summary>Recent log lines, so a UI that subscribes late can still show startup/error messages.</summary>
    public readonly List<string> History = new List<string>();

    private void Log(string line)
    {
        Debug.Log(line);
        History.Add(line);
        if (History.Count > 50) History.RemoveAt(0);
        LogMessage?.Invoke(line);
    }

    // ------------------------------------------------------------------ Platform backends

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

    private void StartBackend()
    {
        uint count = midiInGetNumDevs();
        if (count == 0)
        {
            Log("[MIDI] No MIDI input devices found. Is the piano plugged in and powered on?");
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
        Log("[MIDI] Input devices: " + string.Join(" | ", names));

        if (chosen < 0)
        {
            Log("[MIDI] No device matched '" + deviceNameContains + "'.");
            return;
        }

        _callback = OnWinMidi;
        uint r = midiInOpen(out _handle, (uint)chosen, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
        if (r != 0)
        {
            Log("[MIDI] midiInOpen failed (code " + r + "). Another app may be using the device.");
            _handle = IntPtr.Zero;
            return;
        }
        midiInStart(_handle);
        Log("[MIDI] Listening on: " + names[chosen]);
    }

    private void StopBackend()
    {
        if (_handle == IntPtr.Zero) return;
        midiInStop(_handle);
        midiInReset(_handle);
        midiInClose(_handle);
        _handle = IntPtr.Zero;
    }

    // Runs on a winmm thread: only enqueue, never touch Unity API here.
    private void OnWinMidi(IntPtr hMidiIn, uint msg, IntPtr instance, IntPtr p1, IntPtr p2)
    {
        if (msg == MIM_DATA) _queue.Enqueue((uint)p1.ToInt64());
    }

#elif UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _bridge;

    private void StartBackend()
    {
        Log("[MIDI] Starting Android MIDI backend...");
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                // The Java side reports back through UnitySendMessage to this GameObject.
                _bridge = new AndroidJavaObject("com.jordan.midi.MidiBridge", activity, gameObject.name);
                _bridge.Call("start");
            }
        }
        catch (Exception e)
        {
            Log("[MIDI] Android MIDI bridge failed: " + e.Message);
        }
    }

    private void StopBackend()
    {
        if (_bridge == null) return;
        try { _bridge.Call("stop"); } catch { }
        _bridge.Dispose();
        _bridge = null;
    }
#else
    private void StartBackend() { Log("[MIDI] MidiPianoReader supports Windows and Android only."); }
    private void StopBackend() { }
#endif

    // Called by MidiBridge.java through UnitySendMessage (main thread). Format: "status,data1,data2"
    public void OnAndroidMidi(string csv)
    {
        var p = csv.Split(',');
        if (p.Length < 3) return;
        uint status = uint.Parse(p[0]), d1 = uint.Parse(p[1]), d2 = uint.Parse(p[2]);
        _queue.Enqueue(status | (d1 << 8) | (d2 << 16));
    }

    // Called by MidiBridge.java for connection / error messages.
    public void OnAndroidStatus(string text)
    {
        Log("[MIDI] " + text);
    }

    // ------------------------------------------------------------------ Shared logic

    private void OnEnable() => StartBackend();

    private void OnDisable()
    {
        StopBackend();
        HeldNotes.Clear();
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
                if (logNotes) Log($"[MIDI] Note ON  {NoteName(d1)} (#{d1}) vel {d2} ch {channel}");
                NoteOn?.Invoke(d1, d2);
                _chordTimer = Time.unscaledTime + chordWindow;
                break;
            case 0x80:
            case 0x90: // note-on with velocity 0 == note-off
                HeldNotes.Remove(d1);
                if (logNotes) Log($"[MIDI] Note OFF {NoteName(d1)} (#{d1}) ch {channel}");
                NoteOff?.Invoke(d1);
                if (HeldNotes.Count == 0 && _lastChord != "")
                {
                    _lastChord = "";
                    ChordChanged?.Invoke("");
                }
                break;
            case 0xB0:
                if (logPedalAndOther)
                {
                    string label = d1 == 64 ? "Sustain pedal" : "CC " + d1;
                    Log($"[MIDI] {label}: {d2}" + (d1 == 64 ? (d2 >= 64 ? " (down)" : " (up)") : ""));
                }
                break;
            default:
                if (logPedalAndOther) Log($"[MIDI] status 0x{status:X2} data {d1},{d2}");
                break;
        }
    }

    private void AnnounceChord()
    {
        if (HeldNotes.Count < 2) return;
        var sorted = HeldNotes.OrderBy(n => n).ToList();
        string notes = string.Join(" ", sorted.Select(NoteName));
        string name = IdentifyChord(sorted);
        string line = name != null ? $"{name}  [{notes}]" : $"[{notes}]";
        if (line == _lastChord) return;
        _lastChord = line;
        if (logChords) Log("[MIDI] Chord: " + line);
        ChordChanged?.Invoke(line);
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
            for (int i = 0; i < ChordTypes.Length; i++)
            {
                if (!rel.SetEquals(ChordTypes[i].intervals)) continue;
                // Prefer root == bass note, then simpler chords (earlier in list).
                int score = (root == bass ? 0 : 100) + i;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = NoteNames[root] + ChordTypes[i].suffix + (root != bass ? "/" + NoteNames[bass] : "");
                }
            }
        }
        return best;
    }

    private static string NoteName(int note) => NoteNames[note % 12] + (note / 12 - 1);
}
