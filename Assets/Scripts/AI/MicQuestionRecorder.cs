using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Records one question with Unity's Microphone and ends it on silence, with the same energy VAD rules as the wake word
// plugin: speech = frame RMS above max(minSpeechRms, noiseFloor x vadNoiseRatio); ends after endSilenceSeconds of silence
// once speech was heard, at maxQuestionSeconds, or with nothing (null) if no speech starts within questionStartTimeoutSeconds.
// Used in the Editor and on Android when the wake word plugin is unavailable (the plugin owns the mic while it runs).
public class MicQuestionRecorder : MonoBehaviour, IQuestionRecorder
{
    const int SampleRate = 16000;
    const float FrameSeconds = 0.03f;

    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;
    [Tooltip("Optional. Found automatically. No recording while it is listening.")]
    public VoiceInput voiceInput;

    public bool IsRecording { get; private set; }
    public event Action<byte[]> QuestionRecorded;

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    AudioClip _clip;
    int _readPos, _frameSize, _frequency;
    float _startedAt;
    readonly List<float> _samples = new List<float>();
    float[] _chunk = new float[0];
    int _framePending; // samples of _samples not yet run through the VAD
    float _noiseFloor;
    bool _heard;
    float _silence, _audioSeconds;
    bool _debugClipRunning;

    void Awake()
    {
        if (!voiceInput) voiceInput = FindAnyObjectByType<VoiceInput>();
    }

    public void BeginQuestion()
    {
        if (IsRecording) return;
        if (voiceInput && voiceInput.IsListening) { Debug.LogWarning("[Question] Mic busy (VoiceInput is listening)."); QuestionRecorded?.Invoke(null); return; }
        if (Microphone.devices.Length == 0) { Debug.LogWarning("[Question] No microphone found."); QuestionRecorded?.Invoke(null); return; }
        if (_debugClipRunning) StopAllCoroutines(); // a question beats a debug clip
        _debugClipRunning = false;
        if (Microphone.IsRecording(null)) Microphone.End(null);

        var cfg = Cfg;
        int lengthSec = Mathf.CeilToInt(Mathf.Max(cfg.maxQuestionSeconds, cfg.questionStartTimeoutSeconds)) + 2;
        _clip = Microphone.Start(null, true, lengthSec, SampleRate);
        if (_clip == null) { Debug.LogWarning("[Question] Microphone.Start failed."); QuestionRecorded?.Invoke(null); return; }

        _frequency = _clip.frequency > 0 ? _clip.frequency : SampleRate;
        _frameSize = Mathf.Max(1, Mathf.RoundToInt(_frequency * FrameSeconds));
        _readPos = 0;
        _samples.Clear();
        _framePending = 0;
        _noiseFloor = -1f;
        _heard = false;
        _silence = _audioSeconds = 0f;
        _startedAt = Time.unscaledTime;
        IsRecording = true;
        Debug.Log("[Question] Mic recording (Unity Microphone).");
    }

    public void Cancel()
    {
        if (!IsRecording) return;
        StopMic();
        Debug.Log("[Question] Mic recording cancelled.");
    }

    void Update()
    {
        if (!IsRecording) return;
        var cfg = Cfg;

        int pos = Microphone.GetPosition(null);
        int len = _clip.samples;
        int count = (pos - _readPos + len) % len;
        if (count > 0)
        {
            if (_chunk.Length != count * _clip.channels) _chunk = new float[count * _clip.channels];
            _clip.GetData(_chunk, _readPos); // wraps around the looping clip
            for (int i = 0; i < count; i++) _samples.Add(_chunk[i * _clip.channels]); // first channel
            _readPos = pos;
            _framePending += count;
        }

        // Energy VAD per frame.
        while (_framePending >= _frameSize)
        {
            int start = _samples.Count - _framePending;
            float sum = 0f;
            for (int i = start; i < start + _frameSize; i++) sum += _samples[i] * _samples[i];
            float rms = Mathf.Sqrt(sum / _frameSize);
            _framePending -= _frameSize;
            _audioSeconds += FrameSeconds;

            if (_noiseFloor < 0f) _noiseFloor = Mathf.Min(rms, cfg.minSpeechRms); // the player may already be talking
            bool speech = rms > Mathf.Max(cfg.minSpeechRms, _noiseFloor * cfg.vadNoiseRatio);
            if (speech) { _heard = true; _silence = 0f; }
            else
            {
                _noiseFloor = _noiseFloor * 0.95f + rms * 0.05f; // slow EMA over non-speech frames
                if (_heard) _silence += FrameSeconds;
            }

            if (_heard && _silence >= cfg.endSilenceSeconds) { Finish("end of speech"); return; }
            if (_audioSeconds >= cfg.maxQuestionSeconds) { Finish("max length"); return; }
            if (!_heard && _audioSeconds >= cfg.questionStartTimeoutSeconds) { Finish("no speech"); return; }
        }

        // Safety: the mic never delivered audio.
        if (Time.unscaledTime - _startedAt > cfg.maxQuestionSeconds + 2f) Finish("mic timeout");
    }

    void Finish(string why)
    {
        bool heard = _heard;
        float seconds = _samples.Count / (float)_frequency;
        byte[] wav = heard ? WavUtility.Encode(_samples.ToArray(), _frequency, 1) : null;
        StopMic();
        Debug.Log($"[Question] Mic recording ended ({why}), {seconds:0.0} s, noise floor {Mathf.Max(0f, _noiseFloor):0.0000}" +
                  (heard ? "" : ", nothing heard"));
        QuestionRecorded?.Invoke(wav);
    }

    void StopMic()
    {
        IsRecording = false;
        Microphone.End(null);
        if (_clip) Destroy(_clip);
        _clip = null;
        _samples.Clear();
    }

    // Raw mic audio to a WAV at `path` (Editor check of mic levels). Skipped while a question is being recorded.
    public void SaveDebugClip(string path, float seconds)
    {
        if (IsRecording || _debugClipRunning) { Debug.LogWarning("[Question] Debug clip skipped: mic busy."); return; }
        if (voiceInput && voiceInput.IsListening) { Debug.LogWarning("[Question] Debug clip skipped: VoiceInput is listening."); return; }
        if (Microphone.devices.Length == 0) { Debug.LogWarning("[Question] Debug clip skipped: no microphone."); return; }
        StartCoroutine(DebugClip(path, Mathf.Max(1f, seconds)));
    }

    IEnumerator DebugClip(string path, float seconds)
    {
        _debugClipRunning = true;
        var clip = Microphone.Start(null, false, Mathf.CeilToInt(seconds), SampleRate);
        if (clip == null) { _debugClipRunning = false; yield break; }
        Debug.Log($"[Question] Recording {seconds:0.#} s debug clip...");
        yield return new WaitForSecondsRealtime(seconds + 0.2f);
        Microphone.End(null);
        _debugClipRunning = false;
        try
        {
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            File.WriteAllBytes(path, WavUtility.Encode(data, clip.frequency, clip.channels));
            Debug.Log("[Question] Debug clip saved " + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Question] Debug clip failed: " + e.Message);
        }
        Destroy(clip);
    }

    void OnDisable()
    {
        if (IsRecording) StopMic();
        if (_debugClipRunning) { StopAllCoroutines(); Microphone.End(null); _debugClipRunning = false; }
    }
}
