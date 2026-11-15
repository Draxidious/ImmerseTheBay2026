# Handover: marker calibration into Main_2 (2026-11-15 ~00:30 PT)

For whoever (person or agent) picks this up. Deadline: **Sun 2026-11-15 9:00 AM PT**.

## Goal
Two printed ArUco 4x4_50 markers (33 mm, ID 0 behind A0 on the left rear corner, ID 1 behind C8 on the right) replace the
four pinch steps in `PianoCalibrator`. **Hands, occlusion, the orange recalibrate dot and all XRI interaction stay exactly as in
main** (user requirement; don't invent input or visuals). Print sheet: `output/pdf/Yamaha-P71-Corner-Markers-Letter.pdf`.

## Done
- `Assets/Scripts/UI/MarkerCalibration.cs` + `PianoCalibrator` hooks (`CalibrateFromMarkers`, `ApplyMarkerAdjustment`,
  `CalibrationStarted`, `ShowInfo`, `markersAvailable`, blue `OffsetsDotPressed` dot below the orange one). Details:
  `docs/INTERFACES.md` → PianoCalibrator / MarkerCalibration.
- Setup menu: **Tools → Piano → Add Marker Calibration To Open Scene** (idempotent: disabled `ARTrackedImageManager` +
  library `Assets/Scripts/UI/Markers/PianoCornerMarkers.asset` on the XR Origin, `MarkerCalibration` object, EventSystem with
  `XRUIInputModule`). OpenXR Android: Google `XRSessionFeature` + `XRMarkerTrackingFeature` are on (project-wide, committed).
- Dev_Ari: verified on the Aura (commit `b6aefb5`, merged to main as `33f7876`, pushed). Two scans locked, scale 1.000, offsets
  saved via the slider panel. Rescans differ 8–16 mm (mostly height at the C8 end).
- **Target scene is `Main_2.unity`** (the current app scene; `Main.unity` stays as on remote main). Setup tool run on Main_2,
  offsets baked, editor flow checked. Not committed. `Main_2.unity` is now the enabled build scene (index 0); `Main.unity` is listed but disabled.

## Remaining (in order)
1. ~~Bake the device-saved offsets~~ Done: defaults are the Main-run values left `(-0.005, 0.016, 0.107)`, right
   `(0.015, 0.016, 0.090)` (code + Main/Dev_Ari scenes). The puck also has them in `marker-offsets.json`.
2. Editor check: play Main_2, Enter skips speech/listen, **M** = simulated marker lock → AppFlow should go to ChooseMode, sheet panel
   moves above the keyboard. **O** toggles the offsets panel. Check the console for errors.
3. Docs: CLAUDE.md "Main demo flow" (four-point calibration → markers, pinch fallback) and the Calibration row in
   `docs/AURA_MAIN_TEST.md`; DEVLOG entry.
4. Build Main_2 to the puck (Unity MCP `manage_build` action=build, target android, scenes `["Assets/Scenes/Main_2.unity"]`, output
   `Builds/Main2-markers.apk`, ~5 min), `adb -s V1L264B118612B install -r`, user runs the **whole** flow on the glasses
   (mic, intro, markers, lesson, coach, MIDI). Logs: `adb logcat -d -s Unity | Select-String "MarkerCalibration|PianoCalibrator"`.
5. Commit on main + push (announce the Main edit in team chat; Main is shared, one editor at a time).
6. Optional: same setup tool on `Main.unity` if the team wants markers there too.

## Risks / watch
- Google's `XRSessionFeature` has never run inside Main (worked in Dev_Ari with passthrough + hands). Test the full flow.
- Main has 9 UGUI canvases; adding an EventSystem with `XRUIInputModule` only lets XRI rays hit canvases that have a
  `TrackedDeviceGraphicRaycaster` (only the offsets panel does). Watch for unexpected UI hover/clicks anyway.
- The scene-understanding permission dialog appears during the calibrate line on first launch.
- Fallback to pinches: permission denied, tracker not running after 5 s, or no marker in 45 s.

## Noise to ignore (discard, never commit)
- `Assets/Settings/Project Configuration/UniversalRenderPipelineGlobalSettings.asset` (editor regenerates its runtime list).
- `ProjectSettings/ProjectSettings.asset` `preloadedAssets: []` after a build (build side effect).

## Tooling notes
- Unity MCP: server must be started in the MCP window after each Unity launch; tools load only in a Claude session started after
  it. Fallback used this session: JSON-RPC over HTTP to `http://127.0.0.1:8080/mcp` (initialize → tools/call).
- Glasses capture: `adb shell screencap -d 4626964206039778834 -p /sdcard/x.png`.
- Prototype + full history: `docs/marker-tracking/PIANO_CORNERS_HANDOFF.md`, `PIANO_CORNERS_ITERATIONS.md`
  (AuraMarkerTest project at `C:/Users/Ari/Documents/Projects/AuraMarkerTest`, not a git repo).
- User preferences: use stock Android XR/XRI defaults like main; match main's URP; keep messages short and direct.
