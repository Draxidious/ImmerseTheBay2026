package com.canady.wakeword;

import android.app.Activity;
import android.content.res.AssetManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
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
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * "Hey teacher" wake word: AudioRecord (16 kHz mono) -> sherpa-onnx keyword spotter, on its own thread.
 * The model ships in StreamingAssets/kws (APK assets/kws) and is copied to filesDir/kws on first run,
 * because the sherpa-onnx Java API only loads models from file paths.
 * Sends WakeWordSpike.OnWakeWord("HEY_TEACHER") and OnWakeWordStatus("text") to the Unity object.
 * Step A spike: no VAD, no question recording yet.
 */
public class WakeWordBridge {
    private static final String TAG = "WakeWord";
    private static final int SAMPLE_RATE = 16000;
    // Bump when any file in StreamingAssets/kws changes, so devices recopy the model.
    private static final String MODEL_VERSION = "gigaspeech-3.3M-2024-01-01-int8/1";
    private static final String ASSET_DIR = "kws";
    private static final String ENCODER = "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    private static final String DECODER = "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    private static final String JOINER = "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    private static final String[] MODEL_FILES = { ENCODER, DECODER, JOINER, "tokens.txt", "keywords.txt" };

    private final Activity activity;
    private final String unityObject;
    private volatile boolean running;
    private Thread thread;
    private float keywordsScore = 1.5f;
    private float keywordsThreshold = 0.25f;

    public WakeWordBridge(Activity activity, String unityObject) {
        this.activity = activity;
        this.unityObject = unityObject;
    }

    /** Global defaults; a per-keyword ":score #threshold" in keywords.txt overrides them. Call before start(). */
    public void setTuning(float score, float threshold) {
        keywordsScore = score;
        keywordsThreshold = threshold;
    }

    public synchronized void start() {
        if (running) return;
        running = true;
        thread = new Thread(new Runnable() {
            @Override public void run() { loop(); }
        }, "WakeWord");
        thread.start();
    }

    public synchronized void stop() {
        running = false;
        if (thread == null) return;
        try { thread.join(1000); } catch (InterruptedException ignored) { }
        thread = null;
    }

    private void loop() {
        KeywordSpotter kws = null;
        OnlineStream stream = null;
        AudioRecord rec = null;
        try {
            String dir = copyModel();
            System.loadLibrary("sherpa-onnx-jni");
            LibraryLoader.setAutoLoadEnabled(false); // already loaded; skip the jar's desktop loader
            kws = new KeywordSpotter(buildConfig(dir));
            stream = kws.createStream();
            status("Spotter ready (score " + keywordsScore + ", threshold " + keywordsThreshold + ")");

            int minBytes = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            rec = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(minBytes, SAMPLE_RATE));
            if (rec.getState() != AudioRecord.STATE_INITIALIZED)
                throw new IllegalStateException("AudioRecord failed to initialize (mic permission, or mic busy?)");
            rec.startRecording();
            status("Listening for HEY TEACHER");

            short[] pcm = new short[SAMPLE_RATE / 10]; // 100 ms chunks
            float[] samples = new float[pcm.length];
            double sumSquares = 0;
            int levelCount = 0;
            while (running) {
                int n = rec.read(pcm, 0, pcm.length);
                if (n < 0) throw new IllegalStateException("AudioRecord.read error " + n);
                if (n == 0) continue;
                float[] chunk = n == samples.length ? samples : new float[n];
                for (int i = 0; i < n; i++) {
                    chunk[i] = pcm[i] / 32768f;
                    sumSquares += chunk[i] * chunk[i];
                }
                levelCount += n;
                stream.acceptWaveform(chunk, SAMPLE_RATE);
                while (kws.isReady(stream)) {
                    kws.decode(stream);
                    String keyword = kws.getResult(stream).getKeyword();
                    if (!keyword.isEmpty()) {
                        Log.i(TAG, "DETECTED " + keyword);
                        send("OnWakeWord", keyword);
                        kws.reset(stream); // required after every detection
                    }
                }
                if (levelCount >= SAMPLE_RATE * 5) { // mic level every 5 s, to check the mic is live
                    Log.i(TAG, String.format("mic rms %.4f", Math.sqrt(sumSquares / levelCount)));
                    sumSquares = 0;
                    levelCount = 0;
                }
            }
        } catch (Throwable t) {
            Log.e(TAG, "Wake word disabled", t);
            status("ERROR " + t);
        } finally {
            if (rec != null) {
                try { rec.stop(); } catch (Throwable ignored) { }
                rec.release();
            }
            if (stream != null) stream.release();
            if (kws != null) kws.release();
            running = false;
            status("Stopped");
        }
    }

    private KeywordSpotterConfig buildConfig(String dir) {
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
                .setKeywordsScore(keywordsScore)
                .setKeywordsThreshold(keywordsThreshold)
                .setNumTrailingBlanks(2)
                .setMaxActivePaths(4)
                .build();
    }

    /** Copies assets/kws to filesDir/kws unless the version marker already matches. Returns the directory. */
    private String copyModel() throws Exception {
        File dir = new File(activity.getFilesDir(), ASSET_DIR);
        File marker = new File(dir, "version.txt");
        if (marker.exists() && MODEL_VERSION.equals(readSmall(marker))) {
            boolean complete = true;
            for (String name : MODEL_FILES) complete &= new File(dir, name).length() > 0;
            if (complete) return dir.getAbsolutePath();
        }
        if (!dir.isDirectory() && !dir.mkdirs()) throw new IllegalStateException("Cannot create " + dir);
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
        try (OutputStream out = new FileOutputStream(marker)) { out.write(MODEL_VERSION.getBytes("UTF-8")); }
        status("Copied model to " + dir + " in " + (System.currentTimeMillis() - start) + " ms");
        return dir.getAbsolutePath();
    }

    private static String readSmall(File f) throws Exception {
        byte[] data = new byte[(int) Math.min(f.length(), 256)];
        try (InputStream in = new java.io.FileInputStream(f)) {
            int n = in.read(data);
            return n <= 0 ? "" : new String(data, 0, n, "UTF-8");
        }
    }

    private void status(String text) {
        Log.i(TAG, text);
        send("OnWakeWordStatus", text);
    }

    private void send(String method, String arg) {
        try { UnityPlayer.UnitySendMessage(unityObject, method, arg); }
        catch (Throwable t) { Log.e(TAG, "UnitySendMessage failed", t); }
    }
}
