package com.jordan.midi;

import android.app.Activity;
import android.content.Context;
import android.media.midi.MidiDevice;
import android.media.midi.MidiDeviceInfo;
import android.media.midi.MidiManager;
import android.media.midi.MidiOutputPort;
import android.media.midi.MidiReceiver;
import android.os.Handler;
import android.os.Looper;

import com.unity3d.player.UnityPlayer;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Bridges android.media.midi (USB-MIDI class-compliant keyboards) to Unity.
 * Forwards channel messages to MidiPianoReader.OnAndroidMidi("status,d1,d2")
 * and connection info to MidiPianoReader.OnAndroidStatus("text").
 */
public class MidiBridge {
    private final Activity activity;
    private final String unityObject;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private MidiManager manager;
    private final Map<Integer, MidiDevice> openDevices = new HashMap<>();
    private final List<MidiOutputPort> ports = new ArrayList<>();

    private final MidiManager.DeviceCallback deviceCallback = new MidiManager.DeviceCallback() {
        @Override public void onDeviceAdded(MidiDeviceInfo info) { openDevice(info); }
        @Override public void onDeviceRemoved(MidiDeviceInfo info) {
            status("Device removed: " + deviceName(info));
            openDevices.remove(info.getId());
        }
    };

    public MidiBridge(Activity activity, String unityObject) {
        this.activity = activity;
        this.unityObject = unityObject;
    }

    public void start() {
        activity.runOnUiThread(new Runnable() {
            @Override public void run() {
                android.content.pm.PackageManager pm = activity.getPackageManager();
                status("Features: midi=" + pm.hasSystemFeature("android.software.midi")
                        + " usb.host=" + pm.hasSystemFeature("android.hardware.usb.host")
                        + " ble=" + pm.hasSystemFeature("android.hardware.bluetooth_le"));
                manager = (MidiManager) activity.getSystemService(Context.MIDI_SERVICE);
                if (manager == null) {
                    status("NO Android MIDI service on this device.");
                    return;
                }
                manager.registerDeviceCallback(deviceCallback, handler);
                MidiDeviceInfo[] infos = manager.getDevices();
                status("MIDI devices found: " + infos.length);
                for (MidiDeviceInfo info : infos) openDevice(info);
            }
        });
    }

    public void stop() {
        activity.runOnUiThread(new Runnable() {
            @Override public void run() {
                try {
                    if (manager != null) manager.unregisterDeviceCallback(deviceCallback);
                    for (MidiOutputPort p : ports) { try { p.close(); } catch (Exception e) { } }
                    ports.clear();
                    for (MidiDevice d : openDevices.values()) { try { d.close(); } catch (Exception e) { } }
                    openDevices.clear();
                } catch (Exception e) { }
            }
        });
    }

    private void openDevice(final MidiDeviceInfo info) {
        // "Output ports" of a device are the data coming OUT of the keyboard -> into us.
        if (info.getOutputPortCount() < 1) return;
        if (openDevices.containsKey(info.getId())) return;
        status("Opening: " + deviceName(info));
        manager.openDevice(info, new MidiManager.OnDeviceOpenedListener() {
            @Override public void onDeviceOpened(MidiDevice device) {
                if (device == null) {
                    status("Could not open " + deviceName(info));
                    return;
                }
                openDevices.put(info.getId(), device);
                MidiOutputPort port = device.openOutputPort(0);
                if (port == null) {
                    status("Could not open output port on " + deviceName(info));
                    return;
                }
                ports.add(port);
                port.connect(new Receiver());
                status("Listening on: " + deviceName(info));
            }
        }, handler);
    }

    private class Receiver extends MidiReceiver {
        @Override
        public void onSend(byte[] msg, int offset, int count, long timestamp) {
            int i = offset;
            int end = offset + count;
            while (i < end) {
                int s = msg[i] & 0xFF;
                if (s < 0x80) { i++; continue; }        // stray data byte
                if (s >= 0xF0) {                         // system messages: skip
                    i++;
                    while (i < end && (msg[i] & 0x80) == 0) i++;
                    continue;
                }
                int len = (s >= 0xC0 && s < 0xE0) ? 1 : 2; // program change / channel pressure = 1 data byte
                if (i + len >= end) break;               // truncated message
                int d1 = msg[i + 1] & 0xFF;
                int d2 = (len == 2) ? msg[i + 2] & 0xFF : 0;
                UnityPlayer.UnitySendMessage(unityObject, "OnAndroidMidi", s + "," + d1 + "," + d2);
                i += 1 + len;
            }
        }
    }

    private void status(String text) {
        UnityPlayer.UnitySendMessage(unityObject, "OnAndroidStatus", text);
    }

    private static String deviceName(MidiDeviceInfo info) {
        String name = info.getProperties().getString(MidiDeviceInfo.PROPERTY_NAME);
        return name != null ? name : "MIDI device " + info.getId();
    }
}
