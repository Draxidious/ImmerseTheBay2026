using System.Collections;
using System.Linq;
using UnityEngine;

/// <summary>
/// Debug: plays a song from the <see cref="SongDatabase"/> on the <see cref="PianoOverlay"/> by lighting each key
/// (KeyState.Next) for the length of its note, at the song's tempo. A quick visual check of the notes on the real keyboard.
/// Right-click the component → "Play" / "Stop", or press K in the Editor (toggle with debugKeys).
/// </summary>
public class SongAutoPlayer : MonoBehaviour
{
    [Tooltip("Song id from Assets/Resources/SongDB (e.g. hot_cross_buns, fur_elise).")]
    public string songId = "hot_cross_buns";
    [Tooltip("Section name from the song's sections list. Empty = the whole song.")]
    public string section = "";
    [Tooltip("Optional. Found automatically.")]
    public PianoOverlay piano;
    [Range(0.25f, 2f)] public float tempoScale = 1f;
    public bool debugKeys = true;

    Coroutine _running;

    [ContextMenu("Play")]
    public void Play()
    {
        Stop();
        if (!piano) piano = FindAnyObjectByType<PianoOverlay>();
        var song = string.IsNullOrEmpty(section) ? SongDatabase.Get(songId) : SongDatabase.GetSection(songId, section);
        if (!piano || song == null) { Debug.LogWarning($"[SongAutoPlayer] Need a PianoOverlay and a valid song ({songId})."); return; }
        _running = StartCoroutine(Run(song));
    }

    [ContextMenu("Stop")]
    public void Stop()
    {
        if (_running != null) StopCoroutine(_running);
        _running = null;
        if (piano) piano.ClearAll();
    }

    IEnumerator Run(SongData song)
    {
        float secPerBeat = 60f / Mathf.Max(1, song.tempoBpm) / Mathf.Max(0.05f, tempoScale);
        float Abs(SongNote n) => ((n.bar - 1) * song.beatsPerBar + (n.beat - 1f)) * secPerBeat;
        var notes = song.notes.OrderBy(Abs).ToList();
        float t0 = Time.time - (notes.Count > 0 ? Abs(notes[0]) : 0f); // start right away even if the song starts mid-bar
        var offAt = new System.Collections.Generic.List<(float time, int midi)>();

        int i = 0;
        while (i < notes.Count || offAt.Count > 0)
        {
            float now = Time.time - t0;
            while (i < notes.Count && Abs(notes[i]) <= now)
            {
                piano.SetKey(notes[i].midi, KeyState.Next);
                offAt.Add((Abs(notes[i]) + notes[i].beats * secPerBeat * 0.9f, notes[i].midi));
                i++;
            }
            for (int k = offAt.Count - 1; k >= 0; k--)
                if (offAt[k].time <= now) { piano.SetKey(offAt[k].midi, KeyState.Off); offAt.RemoveAt(k); }
            yield return null;
        }
        _running = null;
    }

#if UNITY_EDITOR
    void Update()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (!debugKeys || kb == null) return;
        if (kb.kKey.wasPressedThisFrame) { if (_running == null) Play(); else Stop(); }
    }
#endif
}
