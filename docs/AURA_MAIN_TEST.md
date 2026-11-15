# Main scene on the Aura — device acceptance test

**The main scene is `Assets/Scenes/Main_2.unity`** (the only enabled build scene, index 0). Use Unity 6000.6.8f1. Device checks
require someone wearing the glasses; do not mark them passed from Editor tests. The older `Main.unity` (35-note wait-mode
Für Elise demo, four-pinch calibration) is disabled; its checks are kept at the end of this file.

## Main_2 checks (verified on the Aura 2026-11-15 ~01:30 unless marked)
| Check | Expected result |
|---|---|
| Startup | `[MarkerCalibration] Loaded offsets …` (if saved before), MIDI backend starts, intro speech. |
| Marker calibration | Prompt asks for the corner markers; first launch asks scene-understanding permission. Look at the LEFT marker (A0 end) then the RIGHT (C8 end), ~1 s each: `Left marker locked`, `Right marker locked; span ~1.25 m`, `Calibrated (markers) … scale 1.000`. Every C lights for 5 s; sheet moves above the keyboard. ✅ |
| Choose mode | `[AppFlow] ChooseMode` right after the marker lock; pick Lesson. ✅ |
| Lesson + MIDI | `[Lesson] Starting …`, demo of measure 1, then runs pass/fail from real P71 notes (`[MIDI] Note ON …`), tempo steps 50 → 75 → 100 %. ✅ |
| Offsets | Blue dot on the back of the LEFT hand opens the slider panel; Save writes `marker-offsets.json`. |
| Recalibrate | Orange dot rescans the markers; lesson keeps going. |
| Fallback | Deny scene permission or hide the markers 45 s: pinch steps come back. |
| Known issue | Logs fill with XRI `IndexOutOfRangeException` (`MaterialHelperBase.GetSharedMaterialForTarget`) from startup; not from the markers. Unfixed. |

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
| Passthrough | Room and real keys remain visible; the glasses do not black out. |
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
