using System;
using System.Collections.Generic;

// The song as the AI layer needs it. Whoever parses the MIDI file fills one of these and hands it to the coach.
// Notes are MIDI note numbers (middle C = 60). Bars and beats are 1-based.

public enum PianoHand { Right, Left }

[Serializable]
public class SongNote
{
    public int midi;
    public int bar;
    public float beat = 1f;   // position inside the bar, 1 = first beat
    public float beats = 1f;  // length in beats
    public PianoHand hand;
    public int finger;        // optional: 1 = thumb ... 5 = little finger, 0 = not specified
}

[Serializable]
public class SongData
{
    public string id;         // stable id, also the cache key for SongFormatter
    public string name;
    public int tempoBpm = 100;
    public int beatsPerBar = 4;
    public List<SongNote> notes = new List<SongNote>();

    public int BarCount
    {
        get
        {
            int last = 0;
            foreach (var n in notes)
                if (n.bar > last) last = n.bar;
            return last;
        }
    }
}
