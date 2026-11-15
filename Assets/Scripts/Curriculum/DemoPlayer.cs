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

    AudioSource source;
    Coroutine routine;
    Action onDone;
    readonly HashSet<ExpectedNote> down = new HashSet<ExpectedNote>();
    readonly Dictionary<int, AudioClip> clips = new Dictionary<int, AudioClip>();

    void Awake()
    {
        // Own child object, so VoiceOutput's GetComponent<AudioSource>() never picks this one.
        var go = new GameObject("DemoAudio");
        go.transform.SetParent(transform, false);
        source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
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
        if (source != null) source.Stop();
    }

    IEnumerator Run(List<ExpectedNote> notes, float secondsPerBeat)
    {
        yield return new WaitForSeconds(0.4f);
        float start = Time.time;
        var ups = new List<(float time, ExpectedNote note)>();

        foreach (var n in notes)
        {
            while (Time.time - start < n.time)
            {
                ReleaseDue(ups, Time.time - start);
                yield return null;
            }
            ReleaseDue(ups, Time.time - start);
            source.PlayOneShot(Clip(n.midi), volume);
            down.Add(n);
            NoteDown?.Invoke(n);
            ups.Add((n.time + Mathf.Max(0.12f, n.duration * secondsPerBeat * 0.9f), n));
        }
        while (ups.Count > 0)
        {
            ReleaseDue(ups, Time.time - start);
            yield return null;
        }
        yield return new WaitForSeconds(0.3f);

        routine = null;
        var done = onDone;
        onDone = null;
        done?.Invoke();
    }

    void ReleaseDue(List<(float time, ExpectedNote note)> ups, float now)
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
