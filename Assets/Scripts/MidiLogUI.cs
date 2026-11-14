using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>Shows the current chord, held notes and a rolling log from a MidiPianoReader on a TMP text.</summary>
public class MidiLogUI : MonoBehaviour
{
    public MidiPianoReader reader;
    public TMP_Text chordText;
    public TMP_Text logText;
    public int maxLines = 12;

    private readonly Queue<string> _lines = new Queue<string>();

    private void OnEnable()
    {
        if (reader == null) reader = FindAnyObjectByType<MidiPianoReader>();
        if (reader == null) return;
        reader.LogMessage += AddLine;
        reader.ChordChanged += OnChord;
        foreach (var l in reader.History) AddLine(l); // replay anything logged before we subscribed
        // The UI shows its own condensed lines, so the reader's note spam isn't duplicated in Debug only.
        if (chordText != null) chordText.text = "-";
    }

    private void OnDisable()
    {
        if (reader == null) return;
        reader.LogMessage -= AddLine;
        reader.ChordChanged -= OnChord;
    }

    private void OnChord(string chord)
    {
        if (chordText != null) chordText.text = string.IsNullOrEmpty(chord) ? "-" : chord;
    }

    private void AddLine(string line)
    {
        _lines.Enqueue(line.Replace("[MIDI] ", ""));
        while (_lines.Count > maxLines) _lines.Dequeue();
        if (logText != null) logText.text = string.Join("\n", _lines);
    }
}
