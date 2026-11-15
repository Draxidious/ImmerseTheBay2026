using UnityEngine;

// Count-in and beat clicks for timed runs. Clicks are scheduled on the audio clock a moment ahead, so they land on the
// beat whatever the frame rate.
public class Metronome : MonoBehaviour
{
    const int SampleRate = 44100;

    [Range(0f, 1f)] public float volume = 0.6f;

    readonly AudioSource[] sources = new AudioSource[2]; // alternate, so scheduling one click never cuts off the last
    int nextSource;
    AudioClip click, accent;

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
        click = MakeClick(1500f);
        accent = MakeClick(2200f);
    }

    // Plays a click at this Time.unscaledTimeAsDouble (now if it has already passed).
    public void ClickAt(double unscaledTime, bool accented)
    {
        var source = sources[nextSource];
        nextSource = (nextSource + 1) % sources.Length;
        source.clip = accented ? accent : click;
        source.volume = volume;
        double delay = unscaledTime - Time.unscaledTimeAsDouble;
        source.PlayScheduled(AudioSettings.dspTime + System.Math.Max(0.0, delay));
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
