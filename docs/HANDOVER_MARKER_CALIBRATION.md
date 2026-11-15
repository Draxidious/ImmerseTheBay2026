# Handover: marker calibration in Main_2 (updated 2026-11-15 ~02:00 PT)

For whoever (person or agent) picks this up. Deadline: **Sun 2026-11-15 9:00 AM PT**.

**Current Main_2 controls.** Tap the raised **Menu** button on the back of the left hand, then choose **Fine Tune**
to open the marker offsets or **Recalibrate** to start another scan. The ring follows the hand while open.
The device test history below predates the hand menu.

## Goal
Two printed ArUco 4x4_50 markers (33 mm, ID 0 behind A0 on the left rear corner, ID 1 behind C8 on the right) replace the
four pinch steps in `PianoCalibrator` when marker tracking works. Print sheet: `output/pdf/Yamaha-P71-Corner-Markers-Letter.pdf`.

## Done
- `Assets/Scripts/UI/MarkerCalibration.cs` + `PianoCalibrator` hooks (`CalibrateFromMarkers`, `ApplyMarkerAdjustment`,
  `CalibrationStarted`, `ShowInfo`, `markersAvailable`, blue `OffsetsDotPressed` dot below the orange one). Details:
  `docs/INTERFACES.md` → PianoCalibrator / MarkerCalibration.
- Setup menu: **Tools → Piano → Add Marker Calibration To Open Scene** (idempotent: disabled `ARTrackedImageManager` +
  library `Assets/Scripts/UI/Markers/PianoCornerMarkers.asset` on the XR Origin, `MarkerCalibration` object, EventSystem with
  `XRUIInputModule`). OpenXR Android: Google `XRSessionFeature` + `XRMarkerTrackingFeature` are on (project-wide, committed).
- Dev_Ari: verified on the Aura (commit `b6aefb5`, merged to main as `33f7876`, pushed). Two scans locked, scale 1.000, offsets
  saved via the slider panel. Rescans differ 8–16 mm (mostly height at the C8 end).
- **Target scene is `Main_2.unity` = THE MAIN SCENE** (only enabled build scene, index 0; `Main.unity` is listed but disabled and
  unchanged). Pushed to main as `1e8919d`. **Verified on the Aura (01:29–01:31):** markers locked in ~6 s (span 1.271 m), scale
  1.000, ChooseMode → Lesson, real MIDI drove measure 1 through the 50/75/100 % steps. Saved offsets loaded from the puck.
- **XRI exception flood fixed** (`1491b8e`) and **keyboard hidden until both markers lock** (`f004cb6`), both pushed.
  **Verified on the Aura (01:48):** 0 `IndexOutOfRange` / 0 other exceptions in 1,280 Unity lines (was ~80k); "Keyboard hidden
  while scanning" → left lock 9 s → right lock 3 s later (span 1.263 m) → "Keyboard shown" → calibrated, scale 1.00 → ChooseMode.
- Offsets in use (code, Main_2, Dev_Ari, and on the puck): left `(-0.005, 0.016, 0.107)`, right `(0.015, 0.016, 0.090)` m
  (inward, down, toward player).

## Remaining
1. ~~XRI `IndexOutOfRangeException` flood~~ Fixed and verified on the Aura: `PianoCalibrator`'s hand occluder replaced the template
   hands' two material slots with ONE, but the rig's poke / near-far hand affordances tint slot 1. It now fills every slot with the
   occluder and keeps the count.
2. ~~Keyboard covers the marker dot during calibration~~ Fixed and verified: hidden until both markers lock.
3. Rescans move the keyboard 8–16 mm (mostly height at the C8 end); retune via **Menu → Fine Tune** if needed.
4. Team: announce that `Main_2` is the main/build scene and that Google's marker OpenXR features are on.

## Risks / watch
- Google's `XRSessionFeature` now runs in Main_2 with passthrough, hands, MIDI and the coach (verified 01:29 and 01:48).
- Main_2 has several UGUI canvases; adding an EventSystem with `XRUIInputModule` only lets XRI rays hit canvases that have a
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
