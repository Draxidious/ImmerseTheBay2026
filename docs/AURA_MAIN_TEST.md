# Main scene on the Aura — device acceptance test

**The main scene is `Assets/Scenes/Main_2.unity`** (the only enabled build scene, index 0). Use Unity 6000.6.8f1. Device checks
require someone wearing the glasses; do not mark them passed from Editor tests. The older `Main.unity` (35-note wait-mode
Für Elise demo, four-pinch calibration) is disabled; its checks are kept at the end of this file.

## Main_2 checks (verified on the Aura 2026-11-15 ~01:30 unless marked)
| Check | Expected result |
|---|---|
| Startup | The welcome introduces Lesson and Free Play. With microphone access and configured transcription, say “audio only” during the intro to skip visual setup. In a separate run, let the intro continue into calibration. |
| Marker calibration | If needed, allow scene access. Follow step 1 behind the far-left key until the amber dot appears, then step 2 behind the far-right key until the key guides appear. Listen for both cues. On a successful scan the sheet moves above the keyboard. |
| Choose mode | Poke **Menu**, pull back, then poke a mode. Try a fast poke through the face. Moving sideways behind the menu or moving the wrist into a stationary index should leave the mode unchanged. |
| Keyboard during scan | `Keyboard hidden while scanning` at the calibrate line; keys appear only with `Keyboard shown` after the right marker locks. ✅ (01:48) |
| Lesson + MIDI | `[Lesson] Starting …`, demo of measure 1, then runs pass/fail from real P71 notes (`[MIDI] Note ON …`), tempo steps 50 → 75 → 100 %. ✅ |
| Sheet readability | In Lesson and Free Play, check the dark notes and staff lines on the white page and the current-bar highlight while wearing the glasses. Check the video capture separately. See [Aura display and sheet music](AURA_DISPLAY.md). |
| Offsets | Select **Fine Tune** in the hand menu. Adjust the offsets, then **Save** to write `marker-offsets.json`. |
| Recalibrate | Poke **Recalibrate Piano**, then poke **Confirm recalibration**. Poke **Close** to cancel. Verify **Restart Lesson** has its own **Confirm restart** button. |
| Fallback | Deny scene permission or hide the markers 45 s: pinch steps come back. |
| No exception flood | No XRI `IndexOutOfRangeException` (`MaterialHelperBase.GetSharedMaterialForTarget`) in the log. Was ~80k lines per run; fixed 2026-11-15 (hand occluder keeps both material slots). ✅ (01:48: 0 exceptions) |

### Run the Editor smoke test before Build and Run

In Unity, choose **Tools → Piano → Run Fingering Smoke Test**. It enters Play mode, opens Main_2,
simulates marker calibration, wrist motion, and right-index poke samples. It checks poke depth, fresh withdrawal between pokes, center cancellation,
confirmation, ray exclusion, fine-tuning, the overlay toggle, coach replay or sheet fallback, and both
mode switches. Check the Console for `[Fingering smoke] PASS` or the failed step. It exits Play mode automatically.

Choose Lesson and check the opening E5 → D♯5 demo for lilac `R5` → `R4` key badges and fingertip glow. During the timed run,
check for a larger, pale-green pulse over the lime glow when the indicated fingertip is on the correct MIDI key.
Fingering defaults off on a fresh install. Poke **Fingering** during Lesson and listen for the first-use instructions, including how to turn it off. Check the small 1–5 numbers on each tracked hand and poke the same button again to clear all cues.

To exit audio-only mode, say **Hey teacher**, wait for the chime, then say **visuals on**. The voice command requires microphone access and working Groq transcription.

Poke outcomes and sampled press pulses persist on the puck. To retrieve them after testing, run
`adb pull /sdcard/Android/data/com.DefaultCompany.VRTemplate/files/interaction-test.log`.
If the log has rotated, retrieve `interaction-test.log.previous` from the same directory.

---

# Old `Main.unity` checks (disabled scene)

## Prepare
1. Connect the Aura puck by USB and authorize debugging. `adb devices -l` should show `device`, not `unauthorized`.
2. Connect the Yamaha P71 to the puck for USB MIDI (use the appropriate hub, or wireless adb on a private network if needed).
3. Build Android to a local APK, install with `adb install -r <apk>`. This preserves app data and existing permission choices.
4. For cloud coaching/transcription, put keys in gitignored `Assets/StreamingAssets/secrets.json` before building.
   Without keys, expect fallback coaching/native Android speech and no transcription. Never distribute a keyed APK.
5. Capture logs in a terminal: `adb logcat -v time Unity:I AndroidRuntime:E '*:S'`.
   Close Direct Preview before running the installed app. Launch from the puck's app list when ready to test.

## Test and record pass/fail
| Check | Expected result |
|---|---|
| First request | Android microphone permission appears before the coach introduction. Allow it; only then does the intro start. |
| First answer | After the intro, orb enters Listening. Say “ready”; recording lasts about 4 s, then calibration begins. With a Groq key, logs show the transcript. |
| Already granted | Relaunch: no mic dialog, intro then recording work normally. |
| Denial / dismissal | Deny or dismiss the dialog: intro still runs, no recording and no second mic prompt, then calibration. |
| Permanent denial | After repeated denial / disabled permission, relaunch still reaches calibration without getting stuck waiting for permission. |
| Optical view | The real room and keys are visible through the lenses. |
| Calibration | Pinch FAR then NEAR ends of lowest key A0, then FAR then NEAR ends of highest key C8. Overlay aligns with real keys. |
| Panel and orb | Sheet sits above the keyboard, readable from the seated position. Orb and text remain visible. |
| MIDI | First expected note E5 (MIDI 76) lights. Press a wrong key: red flash, no advance. Press E5: correct flash and D#5 becomes next. |
| Full run | Follow all 35 notes: sheet highlight advances; completed-run coach feedback appears and speaks (cloud or fallback). |
| Retry | Wait at least 1.5 s, press the first E5 again: a new attempt begins. |
| Recalibration | Touch-and-hold orange dot on back of LEFT hand with right index finger; recalibrate. Sheet moves with the keyboard and lesson progress remains. |

Use Android app settings to reset microphone permission between cases. Do not clear app storage: that also erases calibration.
For a deliberate repeat of the first-request case, after stopping the app, these commands revoke/reset only mic permission:

```powershell
adb shell am force-stop com.DefaultCompany.VRTemplate
adb shell pm revoke com.DefaultCompany.VRTemplate android.permission.RECORD_AUDIO
adb shell pm clear-permission-flags com.DefaultCompany.VRTemplate android.permission.RECORD_AUDIO user-set user-fixed
```

The package ID above is the current project ID; confirm it in the APK if Player Settings changes. Do not run resets while someone is playing.

## Evidence and troubleshooting
- `[AppFlow] Requesting microphone permission...` should precede `[AppFlow] Intro:` on a first request.
- Grant logs `Microphone permission granted`; denial/dismissal logs `continuing without listening`.
- `[PianoCalibrator] Calibrated (4 points)` precedes `[AppFlow] Lesson:`.
- `[Evaluator] ... finished` precedes `[AI] Coach (run_finished)`. Look for `Coach using canned fallback line` if the provider could not reply.
- No MIDI: inspect `[MIDI]` and USB connection. No transcript with fallback: a missing Groq key is expected, not a calibration failure.
- Capture the glasses display (not the puck screen):
  `adb shell screencap -d 4626964206039778834 -p /sdcard/main-test.png`, then `adb pull /sdcard/main-test.png`.
  Verify the display ID if firmware changes.
- Known existing issue: the post-calibration C-key preview can clear a lesson C highlight during its first five seconds.
- Record permission case, last visible step, pass/fail, relevant log lines, and a screenshot for failures. Never include keys.

## Current verification status
Implementation/build results are recorded in `docs/DEVLOG.md`. Hands-on Aura acceptance remains pending until the wearer completes the checklist.
