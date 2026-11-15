using UnityEngine;

// Count-in and beat clicks for timed runs. Clicks are scheduled on the audio clock ahead of time (the engine hands them
// over about half a second early), with one clock offset per run, so they land on the beat whatever the frame rate.
public class Metronome : MonoBehaviour
{
    const int SampleRate = 44100;

    [Range(0f, 1f)] public float volume = 0.6f;

    readonly AudioSource[] sources = new AudioSource[4]; // in turn, so scheduling one click never cuts off another
    int nextSource;
    AudioClip click, accent;
    double dspMinusGame;   // audio clock minus game clock, measured once per run (Anchor)
    bool anchored;

    // Call once when a run's clicks are laid out. Every click of the run is then placed on the audio clock with the
    // same offset, so the spacing between clicks is exact however long the frames are. (Measuring the offset again
    // for each click made every click land a little differently: audible jitter at low frame rates.)
    public void Anchor()
    {
        // The game clock is the time at the start of the frame, the audio clock is "now" and moves in buffer-sized
        // steps, so one direct reading can be off by however long this frame has already taken. The two halves below
        // are tracked over many frames (Update) and are free of that; the direct reading is only the fallback.
        dspMinusGame = samples >= 5 ? realMinusGame + dspMinusReal : AudioSettings.dspTime - Time.unscaledTimeAsDouble;
        anchored = true;
        ResetTracking(); // measure afresh for the next run, so slow drift between the clocks never builds up
    }

    // Smallest (real - game) seen = the reading taken closest to a frame's start. Largest (audio - real) seen = the
    // reading taken right after the audio clock stepped.
    double realMinusGame, dspMinusReal;
    int samples;

    void ResetTracking()
    {
        realMinusGame = double.MaxValue;
        dspMinusReal = double.MinValue;
        samples = 0;
    }

    void Update()
    {
        double real = Time.realtimeSinceStartupAsDouble;
        realMinusGame = System.Math.Min(realMinusGame, real - Time.unscaledTimeAsDouble);
        dspMinusReal = System.Math.Max(dspMinusReal, AudioSettings.dspTime - real);
        samples++;
    }

    void Awake()
    {
        // Own child object, so VoiceOutput's GetComponent<AudioSource>() never picks this one.
        var go = new GameObject("MetronomeAudio");
        go.transform.SetParent(transform, false);
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i] = go.AddComponent<AudioSource>();
            sources[i].playOnAwake = false;
            sources[i].spatialBlend = 0f;
        }
        ResetTracking();
        click = MakeClick(1500f);
        accent = MakeClick(2200f);
    }

    // Plays a click at this Time.unscaledTimeAsDouble (now if it has already passed).
    // volumeScale: 1 = the full volume (count-in), less for the clicks that keep time while the student plays.
    public void ClickAt(double unscaledTime, bool accented, float volumeScale = 1f)
    {
        var source = sources[nextSource];
        nextSource = (nextSource + 1) % sources.Length;
        source.clip = accented ? accent : click;
        source.volume = volume * Mathf.Clamp01(volumeScale);
        if (!anchored) Anchor();
        source.PlayScheduled(System.Math.Max(AudioSettings.dspTime, unscaledTime + dspMinusGame));
    }

    public void Stop()
    {
        foreach (var s in sources)
            if (s != null) s.Stop();
    }

    // A short decaying sine burst: a woodblock-ish tick.
    static AudioClip MakeClick(float freq)
    {
        int length = (int)(SampleRate * 0.05f);
        var data = new float[length];
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / SampleRate;
            data[i] = 0.8f * Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t * 90f);
        }
        var clip = AudioClip.Create("click_" + (int)freq, length, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
