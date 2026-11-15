using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Plays notes for the student to hear and watch: a simple synthesized piano-ish tone per note, plus NoteDown / NoteUp
// events so the key overlay can light the keys (and show the finger) while they sound.
public class DemoPlayer : MonoBehaviour
{
    const int SampleRate = 44100;
    const float ToneSeconds = 1.4f;

    [Range(0f, 1f)] public float volume = 0.5f;

    public event Action<ExpectedNote> NoteDown;
    public event Action<ExpectedNote> NoteUp;

    public bool IsPlaying => routine != null;

    const double Lead = 0.3; // seconds a note is handed to the audio clock before it sounds

    readonly AudioSource[] sources = new AudioSource[12]; // in turn: tones overlap, and a scheduled one must not be replaced
    Coroutine routine;
    Action onDone;
    readonly HashSet<ExpectedNote> down = new HashSet<ExpectedNote>();
    readonly Dictionary<int, AudioClip> clips = new Dictionary<int, AudioClip>();

    void Awake()
    {
        // Own child object, so VoiceOutput's GetComponent<AudioSource>() never picks this one.
        var go = new GameObject("DemoAudio");
        go.transform.SetParent(transform, false);
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i] = go.AddComponent<AudioSource>();
            sources[i].playOnAwake = false;
            sources[i].spatialBlend = 0f;
        }
    }

    // Plays the notes at their ExpectedNote.time. secondsPerBeat sets how long each key stays lit.
    public void Play(List<ExpectedNote> notes, float secondsPerBeat, Action done)
    {
        Stop();
        onDone = done;
        routine = StartCoroutine(Run(notes, secondsPerBeat));
    }

    // Stops now and still calls the done callback, so a skipped demo carries on like a finished one.
    public void Skip()
    {
        var done = onDone;
        Stop();
        done?.Invoke();
    }

    public void Stop()
    {
        if (routine != null) StopCoroutine(routine);
        routine = null;
        onDone = null;
        foreach (var n in down) NoteUp?.Invoke(n);
        down.Clear();
        foreach (var s in sources)
            if (s != null) s.Stop();
    }

    const double MaxStep = 0.1;      // the most the demo moves on in one frame: a longer frame pauses it instead of rushing
    const double StallSeconds = 0.5; // audio clock frozen this long (output asleep): real time moves the keys, silently

    IEnumerator Run(List<ExpectedNote> notes, float secondsPerBeat)
    {
        yield return new WaitForSecondsRealtime(0.4f);
        // One clock for keys and sound: the demo's position, moved each frame by the audio clock, so a key lights as its
        // note sounds. A long frame or the audio waking up moves it at most MaxStep, so no notes rush by unseen; every key
        // stays lit longer than MaxStep, so it is drawn for at least one frame. (On the puck the frame clock fell behind
        // and the keys stayed dark, then all lit at once at the end.)
        double pos = -Lead;
        double dspLast = AudioSettings.dspTime, realLast = Time.realtimeSinceStartupAsDouble, dspMovedAt = realLast;
        var ups = new List<(double time, ExpectedNote note)>();
        int scheduled = 0, lit = 0, capped = 0;
        double longestFrame = 0;
        Debug.Log($"[Demo] Start: {notes.Count} notes, {(notes.Count > 0 ? notes[notes.Count - 1].time : 0):0.00} s, dsp {dspLast:0.000}.");

        while (lit < notes.Count || ups.Count > 0)
        {
            double dsp = AudioSettings.dspTime, real = Time.realtimeSinceStartupAsDouble;
            bool moved = dsp > dspLast;
            if (moved) dspMovedAt = real;
            bool audioAlive = real - dspMovedAt < StallSeconds;
            double step = moved ? dsp - dspLast : audioAlive ? 0 : real - realLast;
            if (step > MaxStep) capped++;
            longestFrame = Math.Max(longestFrame, real - realLast);
            pos += Math.Min(step, MaxStep);
            dspLast = dsp;
            realLast = real;

            while (scheduled < notes.Count && notes[scheduled].time <= pos + Lead)
            {
                var n = notes[scheduled];
                var s = sources[scheduled % sources.Length];
                scheduled++;
                if (!audioAlive || n.time < pos) continue; // no sound now, or its moment passed: the key still lights
                s.clip = Clip(n.midi);
                s.volume = volume;
                s.PlayScheduled(dsp + (n.time - pos));
            }
            while (lit < notes.Count && notes[lit].time <= pos)
            {
                var n = notes[lit++];
                down.Add(n);
                NoteDown?.Invoke(n);
                ups.Add((n.time + Mathf.Max(0.12f, n.duration * secondsPerBeat * 0.9f), n));
                Debug.Log($"[Demo] Key {n.midi} due {n.time:0.00} lit at {pos:0.00} (frame {Time.unscaledDeltaTime * 1000f:0} ms, " +
                          $"dsp {dsp:0.000}{(audioAlive ? "" : ", audio asleep: silent")}).");
            }
            ReleaseDue(ups, pos);
            yield return null;
        }
        Debug.Log($"[Demo] Done: longest frame {longestFrame * 1000:0} ms, {capped} jumps held back.");
        yield return new WaitForSecondsRealtime(0.3f);

        routine = null;
        var done = onDone;
        onDone = null;
        done?.Invoke();
    }

    void ReleaseDue(List<(double time, ExpectedNote note)> ups, double now)
    {
        for (int i = ups.Count - 1; i >= 0; i--)
        {
            if (ups[i].time > now) continue;
            down.Remove(ups[i].note);
            NoteUp?.Invoke(ups[i].note);
            ups.RemoveAt(i);
        }
    }

    // A few decaying harmonics: not a real piano, but clear enough to hear the melody.
    AudioClip Clip(int midi)
    {
        if (clips.TryGetValue(midi, out var clip)) return clip;

        float freq = 440f * Mathf.Pow(2f, (midi - 69) / 12f);
        int length = (int)(SampleRate * ToneSeconds);
        var data = new float[length];
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / SampleRate;
            float attack = Mathf.Clamp01(t / 0.005f);
            float s = Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t * 3f)
                    + 0.5f * Mathf.Sin(4f * Mathf.PI * freq * t) * Mathf.Exp(-t * 5f)
                    + 0.25f * Mathf.Sin(6f * Mathf.PI * freq * t) * Mathf.Exp(-t * 8f);
            data[i] = 0.45f * attack * s;
        }
        clip = AudioClip.Create("note_" + midi, length, 1, SampleRate, false);
        clip.SetData(data, 0);
        clips[midi] = clip;
        return clip;
    }
}
