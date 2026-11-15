package com.canady.wakeword;

import android.app.Activity;
import android.content.res.AssetManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.NoiseSuppressor;
import android.util.Log;

import com.k2fsa.sherpa.onnx.FeatureConfig;
import com.k2fsa.sherpa.onnx.KeywordSpotter;
import com.k2fsa.sherpa.onnx.KeywordSpotterConfig;
import com.k2fsa.sherpa.onnx.LibraryLoader;
import com.k2fsa.sherpa.onnx.OnlineModelConfig;
import com.k2fsa.sherpa.onnx.OnlineStream;
import com.k2fsa.sherpa.onnx.OnlineTransducerModelConfig;
import com.unity3d.player.UnityPlayer;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.ConcurrentLinkedQueue;

/**
 * "Hey teacher" wake word: AudioRecord (16 kHz mono) -> energy VAD -> sherpa-onnx keyword spotter, on one thread.
 * After the keyword (or beginQuestion()) it records the question until end of speech and hands C# a WAV path.
 * The model ships in StreamingAssets/kws (APK assets/kws) and is copied to filesDir/kws,
 * because the sherpa-onnx Java API only loads models from file paths.
 * Callbacks to the Unity object: OnWakeWord, OnQuestionAudio, OnWakeWordStatus, OnWakeWordError.
 * Cooldowns and "is the coach speaking" live in C#; C# calls pause()/resume().
 *
 * Threading: public methods only set volatile fields or queue commands. The audio thread owns AudioRecord,
 * the spotter and the stream, and is the only thread that touches them.
 */
public class WakeWordBridge {
    private static final String TAG = "WakeWord";
    private static final int SAMPLE_RATE = 16000;
    private static final int FRAME = SAMPLE_RATE / 50;           // 20 ms frames for VAD resolution
    private static final int GATE_HANG = SAMPLE_RATE;            // KWS gate closes after 1.0 s without speech
    private static final int MIN_HEARD = SAMPLE_RATE / 10;       // 100 ms of speech after the trigger counts as "heard"
    private static final int LEVEL_LOG_SAMPLES = SAMPLE_RATE * 5;
    private static final int MIC_RETRIES = 5;
    private static final int MIC_RETRY_MS = 200;
    private static final int QUESTION_FILES = 5;                 // question_0..4.wav, rotated
    private static final float MAX_DEBUG_CLIP_SEC = 60f;
    // Bump when any file in StreamingAssets/kws changes. The install time is appended, so reinstalls recopy too.
    private static final String MODEL_VERSION = "gigaspeech-3.3M-2024-01-01-int8/2";
    private static final String ASSET_DIR = "kws";
    private static final String ENCODER = "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    private static final String DECODER = "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    private static final String JOINER = "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    private static final String[] MODEL_FILES = { ENCODER, DECODER, JOINER, "tokens.txt", "keywords.txt" };

    // Commands queued by the control methods, run in order by the audio thread.
    private static final int CMD_PAUSE = 1, CMD_RESUME = 2, CMD_BEGIN = 3, CMD_CANCEL = 4;

    private final Activity activity;
    private final String unityObject;
    private final ConcurrentLinkedQueue<Integer> commands = new ConcurrentLinkedQueue<Integer>();
    private volatile boolean running;
    private volatile int generation;     // bumped by start(); an old thread exits when it no longer matches
    private volatile boolean micSuspended;
    private Thread thread;

    // Settings: read once by the audio thread at start().
    private volatile float keywordsScore = 2.0f;
    private volatile float keywordsThreshold = 0.25f;
    private volatile float minSpeechRms = 0.005f;
    private volatile float noiseRatio = 3.0f;
    private volatile float endSilenceSec = 1.0f;
    private volatile float maxQuestionSec = 8.0f;
    private volatile float questionStartTimeoutSec = 3.0f;
    private volatile float preRollSec = 0.5f;
    private volatile boolean voiceCommunicationSource = false;
    private volatile boolean audioEffects = true;

    // Debug clip request (guarded by "this").
    private String clipRequestPath;
    private int clipRequestSamples;

    public WakeWordBridge(Activity activity, String unityObject) {
        this.activity = activity;
        this.unityObject = unityObject;
    }

    // ---------------------------------------------------------------- setters (apply on next start)

    /** Global KWS boost/threshold; keywords.txt has no per-keyword overrides. */
    public void setTuning(float score, float threshold) {
        keywordsScore = score;
        keywordsThreshold = threshold;
    }

    public void setVad(float minSpeechRms, float noiseRatio, float endSilenceSec, float maxQuestionSec,
                       float questionStartTimeoutSec, float preRollSec) {
        this.minSpeechRms = Math.max(0f, minSpeechRms);
        this.noiseRatio = Math.max(1f, noiseRatio);
        this.endSilenceSec = Math.max(0.1f, endSilenceSec);
        this.maxQuestionSec = Math.max(0.5f, maxQuestionSec);
        this.questionStartTimeoutSec = Math.max(0.1f, questionStartTimeoutSec);
        this.preRollSec = Math.max(0f, Math.min(5f, preRollSec));
    }

    /** source: false = VOICE_RECOGNITION, true = VOICE_COMMUNICATION. audioEffects: AEC + NS when available. */
    public void setAudio(boolean voiceCommunicationSource, boolean audioEffects) {
        this.voiceCommunicationSource = voiceCommunicationSource;
        this.audioEffects = audioEffects;
    }

    // ---------------------------------------------------------------- lifecycle

    public synchronized void start() {
        try {
            if (running) return;
            commands.clear();
            micSuspended = false;
            final int myGen = ++generation;
            final Thread previous = thread; // may still be shutting down after a stop() that timed out
            running = true;
            thread = new Thread(new Runnable() {
                @Override public void run() {
                    if (previous != null) {
                        try { previous.join(); } catch (InterruptedException ignored) { }
                    }
                    loop(myGen);
                }
            }, "WakeWord");
            thread.start();
        } catch (Throwable t) {
            running = false;
            fail("start failed", t);
        }
    }

    public synchronized void stop() {
        try {
            running = false;
            if (thread == null) return;
            try { thread.join(1000); } catch (InterruptedException ignored) { }
            if (!thread.isAlive()) thread = null; // else start() makes the next thread wait for it
        } catch (Throwable t) {
            Log.e(TAG, "stop failed", t);
        }
    }

    public void pause() { commands.add(CMD_PAUSE); }

    public void resume() { commands.add(CMD_RESUME); }

    public void suspendMic() { micSuspended = true; }

    public void resumeMic() { micSuspended = false; }

    /** Starts a question capture now (no keyword, no pre-roll). Also ends a pause. */
    public void beginQuestion() {
        if (!running) {
            status("beginQuestion ignored: not running");
            send("OnQuestionAudio", "");
            return;
        }
        commands.add(CMD_BEGIN);
    }

    public void cancelQuestion() { commands.add(CMD_CANCEL); }

    /** Writes the next `seconds` of raw AudioRecord output to `path` (16 kHz mono PCM16 WAV). */
    public void saveDebugClip(String path, float seconds) {
        try {
            if (path == null || path.isEmpty() || !(seconds > 0)) {
                status("debug clip ignored: bad path or length");
                return;
            }
            synchronized (this) {
                clipRequestPath = path;
                clipRequestSamples = (int) (Math.min(seconds, MAX_DEBUG_CLIP_SEC) * SAMPLE_RATE);
            }
            Log.i(TAG, "debug clip requested: " + seconds + " s -> " + path);
        } catch (Throwable t) {
            Log.e(TAG, "saveDebugClip failed", t);
        }
    }

    // ---------------------------------------------------------------- audio thread state

    private KeywordSpotter kws;
    private OnlineStream stream;
    private AudioRecord rec;
    private AcousticEchoCanceler aec;
    private NoiseSuppressor ns;

    private boolean paused;
    private boolean gateOpen;
    private int gateSilence;
    private boolean capturing;
    private EndOfSpeech endOfSpeech;
    private short[] capBuf;
    private int capLen;
    private int questionCount;
    private Ring ring;
    private Vad vad;
    private float[] floatBuf;

    private short[] clipBuf;
    private int clipLen;
    private String clipPath;

    private float sEndSilence, sMaxQuestion, sStartTimeout;
    private boolean sVoiceComm, sEffects;

    private boolean alive(int myGen) { return running && generation == myGen; }

    private void loop(int myGen) {
        try {
            float score = keywordsScore, threshold = keywordsThreshold;
            sEndSilence = endSilenceSec;
            sMaxQuestion = maxQuestionSec;
            sStartTimeout = questionStartTimeoutSec;
            sVoiceComm = voiceCommunicationSource;
            sEffects = audioEffects;
            int preRoll = (int) (preRollSec * SAMPLE_RATE);
            vad = new Vad(minSpeechRms, noiseRatio);
            ring = new Ring(preRoll);
            capBuf = new short[preRoll + (int) (sMaxQuestion * SAMPLE_RATE) + FRAME];
            floatBuf = new float[Math.max(preRoll, FRAME)];
            paused = false;
            capturing = false;
            gateOpen = false;

            String dir = copyModel();
            System.loadLibrary("sherpa-onnx-jni");
            LibraryLoader.setAutoLoadEnabled(false); // already loaded; skip the jar's desktop loader
            kws = new KeywordSpotter(buildConfig(dir, score, threshold));
            stream = kws.createStream();
            status("Spotter ready (score " + score + ", threshold " + threshold + ")");
            if (!alive(myGen)) return;

            openMic(myGen);
            status("Listening for HEY TEACHER (vad min " + vad.minSpeechRms + " ratio " + vad.noiseRatio
                    + ", end silence " + sEndSilence + " s, max " + sMaxQuestion + " s, pre-roll " + preRollSec + " s)");

            short[] pcm = new short[FRAME];
            double peak = 0;
            int levelCount = 0;
            int readErrors = 0;
            while (alive(myGen)) {
                processCommands();

                if (micSuspended) {
                    if (rec != null) {
                        closeMic();
                        cancelCapture("mic suspended");
                        resetSpotting();
                        status("Mic suspended");
                    }
                    Thread.sleep(50);
                    continue;
                }
                if (rec == null) {
                    openMic(myGen);
                    resetSpotting();
                    status("Mic resumed");
                    continue;
                }

                int n = rec.read(pcm, 0, pcm.length);
                if (n < 0) {
                    if (++readErrors > 3) throw new IllegalStateException("AudioRecord.read keeps failing: " + n);
                    status("AudioRecord.read error " + n + ", reopening mic");
                    closeMic();
                    cancelCapture("mic error");
                    continue; // reopened at the top of the loop (5 retries, then fatal)
                }
                if (n == 0) continue;
                readErrors = 0;

                debugClip(pcm, n);
                if (paused) continue;

                float rms = rms(pcm, n);
                boolean speech = vad.isSpeech(rms);
                peak = Math.max(peak, rms);
                levelCount += n;
                if (levelCount >= LEVEL_LOG_SAMPLES) { // mic level every 5 s, to check the mic is live
                    Log.i(TAG, String.format("mic rms floor %.4f peak %.4f", vad.floor, peak));
                    peak = 0;
                    levelCount = 0;
                }

                ring.push(pcm, n);
                if (capturing) captureFrame(pcm, n, speech);
                else spotFrame(pcm, n, speech);
            }
        } catch (Throwable t) {
            if (alive(myGen)) {
                Log.e(TAG, "Wake word disabled", t);
                releaseAll();
                if (generation == myGen) running = false;
                send("OnWakeWordError", t.toString());
                return;
            }
            Log.w(TAG, "Error while stopping: " + t);
        } finally {
            releaseAll();
        }
        Log.i(TAG, "Stopped");
        send("OnWakeWordStatus", "Stopped");
    }

    private void processCommands() {
        Integer cmd;
        while ((cmd = commands.poll()) != null) {
            switch (cmd) {
                case CMD_PAUSE:
                    if (!paused) Log.i(TAG, "paused");
                    paused = true;
                    cancelCapture("paused");
                    resetSpotting();
                    break;
                case CMD_RESUME:
                    if (paused) Log.i(TAG, "resumed");
                    paused = false;
                    cancelCapture("resumed");
                    resetSpotting();
                    ring.clear(); // don't feed audio from before the pause
                    break;
                case CMD_BEGIN:
                    if (capturing) { Log.i(TAG, "beginQuestion ignored: already capturing"); break; }
                    if (rec == null || micSuspended) {
                        status("beginQuestion: mic suspended, no audio");
                        send("OnQuestionAudio", "");
                        break;
                    }
                    paused = false;
                    resetSpotting();
                    startCapture(false);
                    Log.i(TAG, "CAPTURING (beginQuestion)");
                    break;
                case CMD_CANCEL:
                    cancelCapture("cancelQuestion");
                    break;
                default:
                    break;
            }
        }
    }

    // SPOTTING: feed the KWS only while the VAD gate is open.
    private void spotFrame(short[] pcm, int n, boolean speech) {
        if (speech) {
            gateSilence = 0;
            if (!gateOpen) {
                gateOpen = true;
                Log.d(TAG, "gate open");
                int count = ring.copyTo(capBuf, 0); // ring already holds this frame; capBuf is free while spotting
                feed(capBuf, count);
            } else {
                feed(pcm, n);
            }
        } else if (gateOpen) {
            feed(pcm, n);
            gateSilence += n;
            if (gateSilence >= GATE_HANG) {
                gateOpen = false;
                kws.reset(stream);
                Log.d(TAG, "gate closed");
                return;
            }
        } else {
            return;
        }
        while (kws.isReady(stream)) {
            kws.decode(stream);
            String keyword = kws.getResult(stream).getKeyword();
            if (keyword != null && !keyword.isEmpty()) {
                Log.i(TAG, "DETECTED " + keyword);
                resetSpotting(); // kws.reset is required after every detection
                startCapture(true);
                Log.i(TAG, "CAPTURING (keyword, pre-roll " + capLen + " samples)");
                send("OnWakeWord", keyword);
                return;
            }
        }
    }

    private void feed(short[] pcm, int n) {
        for (int off = 0; off < n; off += floatBuf.length) {
            int len = Math.min(floatBuf.length, n - off);
            float[] chunk = len == floatBuf.length ? floatBuf : new float[len];
            for (int i = 0; i < len; i++) chunk[i] = pcm[off + i] / 32768f;
            stream.acceptWaveform(chunk, SAMPLE_RATE);
        }
    }

    private void resetSpotting() {
        gateOpen = false;
        gateSilence = 0;
        if (kws != null && stream != null) kws.reset(stream);
    }

    private void startCapture(boolean withPreRoll) {
        capLen = withPreRoll ? ring.copyTo(capBuf, 0) : 0;
        endOfSpeech = new EndOfSpeech((int) (sEndSilence * SAMPLE_RATE), (int) (sMaxQuestion * SAMPLE_RATE),
                (int) (sStartTimeout * SAMPLE_RATE), MIN_HEARD);
        capturing = true;
    }

    private void captureFrame(short[] pcm, int n, boolean speech) {
        int room = Math.min(n, capBuf.length - capLen);
        System.arraycopy(pcm, 0, capBuf, capLen, room);
        capLen += room;
        int result = endOfSpeech.update(speech, n);
        if (result == EndOfSpeech.CONTINUE) return;
        capturing = false;
        resetSpotting();
        if (result == EndOfSpeech.DONE_EMPTY) {
            Log.i(TAG, "question: no speech heard");
            send("OnQuestionAudio", "");
            return;
        }
        File out = new File(activity.getCacheDir(), "question_" + (questionCount++ % QUESTION_FILES) + ".wav");
        try {
            writeWav(out, capBuf, capLen, SAMPLE_RATE);
        } catch (IOException e) {
            Log.e(TAG, "question WAV write failed", e);
            status("question WAV write failed: " + e);
            send("OnQuestionAudio", "");
            return;
        }
        Log.i(TAG, String.format("question saved %s (%.2f s)", out.getAbsolutePath(), capLen / (float) SAMPLE_RATE));
        send("OnQuestionAudio", out.getAbsolutePath());
    }

    private void cancelCapture(String why) {
        if (!capturing) return;
        capturing = false;
        capLen = 0;
        Log.i(TAG, "capture cancelled (" + why + ")");
    }

    private void debugClip(short[] pcm, int n) {
        if (clipBuf == null) {
            synchronized (this) {
                if (clipRequestPath == null) return;
                clipPath = clipRequestPath;
                clipBuf = new short[clipRequestSamples];
                clipLen = 0;
                clipRequestPath = null;
            }
        }
        int room = Math.min(n, clipBuf.length - clipLen);
        System.arraycopy(pcm, 0, clipBuf, clipLen, room);
        clipLen += room;
        if (clipLen < clipBuf.length) return;
        try {
            File f = new File(clipPath);
            File parent = f.getParentFile();
            if (parent != null && !parent.isDirectory()) parent.mkdirs();
            writeWav(f, clipBuf, clipLen, SAMPLE_RATE);
            status("debug clip saved " + clipPath);
        } catch (IOException e) {
            Log.e(TAG, "debug clip write failed", e);
            status("debug clip failed " + clipPath + ": " + e);
        }
        clipBuf = null;
        clipPath = null;
    }

    // ---------------------------------------------------------------- mic

    private void openMic(int myGen) throws Exception {
        int source = sVoiceComm ? MediaRecorder.AudioSource.VOICE_COMMUNICATION : MediaRecorder.AudioSource.VOICE_RECOGNITION;
        int minBytes = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
        int bytes = Math.max(minBytes, SAMPLE_RATE * 2); // 1 s
        String lastError = "";
        // Unity's Microphone.End() may still be releasing the mic, so retry a few times.
        for (int attempt = 1; attempt <= MIC_RETRIES && alive(myGen); attempt++) {
            AudioRecord r = null;
            try {
                r = new AudioRecord(source, SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bytes);
                if (r.getState() == AudioRecord.STATE_INITIALIZED) {
                    r.startRecording();
                    if (r.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) {
                        rec = r;
                        enableEffects();
                        Log.i(TAG, "AudioRecord open (" + (sVoiceComm ? "VOICE_COMMUNICATION" : "VOICE_RECOGNITION")
                                + ", attempt " + attempt + ")");
                        return;
                    }
                    lastError = "startRecording did not start (mic busy?)";
                } else {
                    lastError = "AudioRecord failed to initialize (mic permission, or mic busy?)";
                }
            } catch (Throwable t) {
                lastError = t.toString();
            }
            if (r != null) {
                try { r.release(); } catch (Throwable ignored) { }
            }
            Log.w(TAG, "mic open attempt " + attempt + " failed: " + lastError);
            Thread.sleep(MIC_RETRY_MS);
        }
        if (!alive(myGen)) return; // stopping; the loop exits
        throw new IllegalStateException(lastError);
    }

    private void enableEffects() {
        if (!sEffects) { status("Audio effects off"); return; }
        String aecState = "unavailable", nsState = "unavailable";
        int session = rec.getAudioSessionId();
        try {
            if (AcousticEchoCanceler.isAvailable()) {
                aec = AcousticEchoCanceler.create(session);
                if (aec != null) { aec.setEnabled(true); aecState = aec.getEnabled() ? "on" : "off"; }
                else aecState = "create failed";
            }
        } catch (Throwable t) { aecState = "error " + t; }
        try {
            if (NoiseSuppressor.isAvailable()) {
                ns = NoiseSuppressor.create(session);
                if (ns != null) { ns.setEnabled(true); nsState = ns.getEnabled() ? "on" : "off"; }
                else nsState = "create failed";
            }
        } catch (Throwable t) { nsState = "error " + t; }
        status("Audio effects: AEC " + aecState + ", NS " + nsState);
    }

    private void closeMic() {
        if (aec != null) { try { aec.release(); } catch (Throwable ignored) { } aec = null; }
        if (ns != null) { try { ns.release(); } catch (Throwable ignored) { } ns = null; }
        if (rec != null) {
            try { rec.stop(); } catch (Throwable ignored) { }
            try { rec.release(); } catch (Throwable ignored) { }
            rec = null;
        }
    }

    private void releaseAll() {
        closeMic();
        capturing = false;
        if (stream != null) { try { stream.release(); } catch (Throwable ignored) { } stream = null; }
        if (kws != null) { try { kws.release(); } catch (Throwable ignored) { } kws = null; }
    }

    // ---------------------------------------------------------------- model

    private KeywordSpotterConfig buildConfig(String dir, float score, float threshold) {
        FeatureConfig feat = FeatureConfig.builder().setSampleRate(SAMPLE_RATE).setFeatureDim(80).build();
        OnlineTransducerModelConfig transducer = OnlineTransducerModelConfig.builder()
                .setEncoder(dir + "/" + ENCODER)
                .setDecoder(dir + "/" + DECODER)
                .setJoiner(dir + "/" + JOINER)
                .build();
        OnlineModelConfig model = OnlineModelConfig.builder()
                .setTransducer(transducer)
                .setTokens(dir + "/tokens.txt")
                .setModelType("zipformer2")
                .setNumThreads(1)
                .setProvider("cpu")
                .setDebug(false)
                .build();
        return KeywordSpotterConfig.builder()
                .setFeatureConfig(feat)
                .setOnlineModelConfig(model)
                .setKeywordsFile(dir + "/keywords.txt")
                .setKeywordsScore(score)
                .setKeywordsThreshold(threshold)
                .setNumTrailingBlanks(2)
                .setMaxActivePaths(4)
                .build();
    }

    /** Copies assets/kws to filesDir/kws unless the marker (version + install time) matches. Returns the directory. */
    private String copyModel() throws Exception {
        File dir = new File(activity.getFilesDir(), ASSET_DIR);
        File marker = new File(dir, "version.txt");
        long installed = 0;
        try {
            installed = activity.getPackageManager().getPackageInfo(activity.getPackageName(), 0).lastUpdateTime;
        } catch (Throwable t) {
            Log.w(TAG, "No PackageInfo.lastUpdateTime: " + t);
        }
        String expected = MODEL_VERSION + "|" + installed;
        if (marker.exists() && expected.equals(readSmall(marker))) {
            boolean complete = true;
            for (String name : MODEL_FILES) complete &= new File(dir, name).length() > 0;
            if (complete) return dir.getAbsolutePath();
        }
        if (!dir.isDirectory() && !dir.mkdirs()) throw new IllegalStateException("Cannot create " + dir);
        marker.delete(); // a half-finished copy must not look valid
        AssetManager assets = activity.getAssets();
        long start = System.currentTimeMillis();
        byte[] buffer = new byte[64 * 1024];
        for (String name : MODEL_FILES) {
            try (InputStream in = assets.open(ASSET_DIR + "/" + name);
                 OutputStream out = new FileOutputStream(new File(dir, name))) {
                int n;
                while ((n = in.read(buffer)) > 0) out.write(buffer, 0, n);
            }
        }
        try (OutputStream out = new FileOutputStream(marker)) { out.write(expected.getBytes("UTF-8")); }
        status("Copied model to " + dir + " in " + (System.currentTimeMillis() - start) + " ms");
        return dir.getAbsolutePath();
    }

    private static String readSmall(File f) throws Exception {
        byte[] data = new byte[(int) Math.min(f.length(), 256)];
        try (InputStream in = new FileInputStream(f)) {
            int n = in.read(data);
            return n <= 0 ? "" : new String(data, 0, n, "UTF-8");
        }
    }

    // ---------------------------------------------------------------- Unity messages

    private void fail(String what, Throwable t) {
        Log.e(TAG, what, t);
        send("OnWakeWordError", what + ": " + t);
    }

    private void status(String text) {
        Log.i(TAG, text);
        send("OnWakeWordStatus", text);
    }

    private void send(String method, String arg) {
        try { UnityPlayer.UnitySendMessage(unityObject, method, arg); }
        catch (Throwable t) { Log.e(TAG, "UnitySendMessage failed", t); }
    }

    // ---------------------------------------------------------------- pure helpers (no Android calls; desktop-testable)

    static float rms(short[] pcm, int n) {
        double sum = 0;
        for (int i = 0; i < n; i++) { double s = pcm[i] / 32768.0; sum += s * s; }
        return n == 0 ? 0f : (float) Math.sqrt(sum / n);
    }

    /** Energy VAD: speech if rms > max(minSpeechRms, noiseFloor * noiseRatio). The floor is a slow EMA of quiet frames. */
    static final class Vad {
        static final float QUIET_ALPHA = 0.01f;   // ~2 s time constant at 20 ms frames
        static final float SPEECH_ALPHA = 0.001f; // ~20 s: lets the floor climb out of a sudden loud steady noise
        final float minSpeechRms, noiseRatio;
        float floor = -1f;

        Vad(float minSpeechRms, float noiseRatio) {
            this.minSpeechRms = minSpeechRms;
            this.noiseRatio = noiseRatio;
        }

        boolean isSpeech(float rms) {
            if (floor < 0) floor = Math.min(rms, minSpeechRms / noiseRatio); // start no higher than the fixed minimum allows
            boolean speech = rms > Math.max(minSpeechRms, floor * noiseRatio);
            float a = speech ? SPEECH_ALPHA : QUIET_ALPHA;
            floor += (rms - floor) * a;
            if (floor < 1e-6f) floor = 1e-6f;
            return speech;
        }
    }

    /** End-of-question logic in samples. Only frames after the trigger are passed in (pre-roll doesn't count). */
    static final class EndOfSpeech {
        static final int CONTINUE = 0, DONE_SPEECH = 1, DONE_EMPTY = 2;
        final int endSilence, maxSamples, startTimeout, minHeard;
        int elapsed, speechSamples, silence;

        EndOfSpeech(int endSilence, int maxSamples, int startTimeout, int minHeard) {
            this.endSilence = endSilence;
            this.maxSamples = maxSamples;
            this.startTimeout = startTimeout;
            this.minHeard = minHeard;
        }

        boolean heard() { return speechSamples >= minHeard; }

        int update(boolean speech, int n) {
            elapsed += n;
            if (speech) { speechSamples += n; silence = 0; }
            else silence += n;
            if (heard() && silence >= endSilence) return DONE_SPEECH;
            if (elapsed >= maxSamples) return heard() ? DONE_SPEECH : DONE_EMPTY;
            if (!heard() && elapsed >= startTimeout) return DONE_EMPTY;
            return CONTINUE;
        }
    }

    /** Ring buffer of the last N samples (pre-roll). */
    static final class Ring {
        final short[] buf;
        int pos, count;

        Ring(int size) { buf = new short[Math.max(0, size)]; }

        void clear() { pos = 0; count = 0; }

        void push(short[] pcm, int n) {
            if (buf.length == 0) return;
            for (int i = 0; i < n; i++) {
                buf[pos] = pcm[i];
                pos = (pos + 1) % buf.length;
            }
            count = Math.min(buf.length, count + n);
        }

        /** Copies the contents oldest first; returns the sample count. */
        int copyTo(short[] dst, int off) {
            int start = (pos - count + buf.length) % Math.max(1, buf.length);
            for (int i = 0; i < count; i++) dst[off + i] = buf[(start + i) % buf.length];
            return count;
        }
    }

    /** 16-bit PCM mono WAV. */
    static void writeWav(File f, short[] pcm, int n, int sampleRate) throws IOException {
        byte[] out = new byte[44 + n * 2];
        int dataBytes = n * 2;
        putAscii(out, 0, "RIFF");
        putInt(out, 4, 36 + dataBytes);
        putAscii(out, 8, "WAVE");
        putAscii(out, 12, "fmt ");
        putInt(out, 16, 16);              // fmt chunk size
        putShort(out, 20, 1);             // PCM
        putShort(out, 22, 1);             // mono
        putInt(out, 24, sampleRate);
        putInt(out, 28, sampleRate * 2);  // byte rate
        putShort(out, 32, 2);             // block align
        putShort(out, 34, 16);            // bits per sample
        putAscii(out, 36, "data");
        putInt(out, 40, dataBytes);
        for (int i = 0; i < n; i++) putShort(out, 44 + i * 2, pcm[i]);
        try (OutputStream os = new FileOutputStream(f)) { os.write(out); }
    }

    private static void putAscii(byte[] b, int off, String s) {
        for (int i = 0; i < 4; i++) b[off + i] = (byte) s.charAt(i);
    }

    private static void putInt(byte[] b, int off, int v) {
        b[off] = (byte) v; b[off + 1] = (byte) (v >> 8); b[off + 2] = (byte) (v >> 16); b[off + 3] = (byte) (v >> 24);
    }

    private static void putShort(byte[] b, int off, int v) {
        b[off] = (byte) v; b[off + 1] = (byte) (v >> 8);
    }
}
