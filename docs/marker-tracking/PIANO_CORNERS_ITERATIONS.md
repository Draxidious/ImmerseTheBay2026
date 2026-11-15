# Piano corners: iteration history (2026-11-14)

How the marker-based keyboard line in AuraMarkerTest got to its current state. This covers one evening of device testing on the XREAL Aura (puck `V1L264B118612B`). Each iteration lists what changed, what the puck logs showed, and what we learned. The current setup is in `PIANO_CORNERS_HANDOFF.md`.

## Summary

| # | Build (approx. PT) | What changed | Outcome |
|---|---|---|---|
| 0 | before 22:34 | Two ArUco markers → line at the **front** key corners; right-hand pinch-drag; save/reset dots on the left wrist | Markers locked; hands never responded |
| 1 | 22:43 | Hand diagnostics: joint dots, HUD hand status, `Hands …` log, subsystem restart after permission grant | Unity hand data confirmed |
| 2 | 22:57 | Line moved to the **back** edge of the keys (offsets from photos); thin line + end ticks; custom poke-slider panel | Locked fine (span 1.269 m); panel never used |
| 3 | 23:06 | Custom pinch ray, `<` `>` arrows, 88-key preview | Rejected: custom input instead of Android XR defaults |
| 4 | 23:16 | Stock **XRI hands rig** grafted in; standard UGUI panel; key preview off by default | Hands pink, too bright, one eye only |
| 5 | 23:22 | Detours: hid the hands, then the main app's occluder, then a Built-in grey shader | All reverted; broke hand tracking visuals |
| 6 | 23:32 | Project switched to **URP** (same asset as the main app); whole rig re-grafted; panel layout fixed | Left dot appears below the marker; line too low |
| 7 | 23:36 | Stricter marker lock, span gate 1.18–1.32 m, true-centre dot, sliders ±60 mm | Current build; on-glasses check pending |

## Iteration 0: starting point

- **Design:**
  - `RuneLine` locks ArUco 4x4_50 ID 0 then ID 1 (33 mm symbols).
  - Default offsets aimed at the **front** outer corners of A0/C8: toward player 0.230 / 0.237 m, inward 0.009 / 0.013 m, down 0.025 m.
  - Pinching with the right hand near an endpoint dragged it.
  - Green (save) and orange (reset) dots on the back of the left wrist responded to a touch-and-hold.
- **Logs:**
  - Both markers locked (span 1.24 m).
  - In the first run the `HAND_TRACKING` permission was granted only at 22:34:24, about 5 s after the hand subsystem had started.
  - The system hand tracker saw hands for about 2 s. Then `GNavViewModel: GNav is visible` appeared: a pinch with the palm facing the face opens the **Android XR system menu**, which takes focus away from the app. The app was then closed.
  - The second run hit the same menu before locking.
  - `RuneLine` logged nothing about hands, so it couldn't be told whether Unity was getting joint data.
- **Lesson:** on Android XR a palm-toward-face pinch belongs to the system. Hand-related code needs its own logging.

## Iteration 1: hand diagnostics

- **Change:**
  - Dots on all 26 joints of each hand.
  - A HUD line `Hands: L ok R ok`.
  - A throttled `[RuneLine] Hands subsystems=… running=… left=… right=…` log.
  - A permission-grant callback that restarts `XRHandSubsystem` on the main thread.
- **Logs:** `subsystems=1 running=True left=True right=True`, so Unity was receiving hand data with the permission granted.
- **User feedback:**
  - The drag worked but was too sensitive; they'd prefer a slider.
  - The save dot never appeared, because it required both markers locked, the left hand tracked, and the back of the wrist facing the glasses.
  - The **line was in the wrong place**. They sent annotated photos with a red line at the back edge of the white keys.

## Iteration 2: line at the back edge, slider panel (custom poke)

- **Offsets measured from the photos:**
  - White-key pitch 23.5 mm used as the scale, with parallax corrected for the markers being about 25 mm higher than the keys.
  - Left `(0.009, 0.025, 0.088)`, right `(0.010, 0.025, 0.076)`.
  - The close-ups and the overview agreed within a few mm.
  - The default `lineLift` changed from 4 mm to 1.5 mm.
- **Rendering:**
  - The line core went from 3.5–6 mm to 1.5–2.5 mm.
  - The 22 mm endpoint spheres were replaced by 2 × 18 mm ticks across the key, so the start and end are unambiguous.
- **Panel:** five rows (Left/Right depth, Height, Left/Right end), each covering ±30 mm over a 20 cm track (about 3.3:1 finger-to-line). Save and Rescan were on the panel. The wrist save was removed.
- **Logs:**
  - Locked at 23:00 with `Marker span=1.269 m`, `key span=1.250 m` (expected about 1.222 m).
  - No `Grabbed slider` line at all, so the poke panel was never actually operated.
  - Hand tracking dropped in and out often.

## Iteration 3: custom ray and arrows (rejected)

- **Change:**
  - A hand-rolled pinch ray: a ray from an estimated shoulder through the index knuckle, plus my own thumb–index distance thresholds.
  - Cursor and ray visuals.
  - `<` `>` arrows with 1 mm steps and auto-repeat.
  - An 88-key preview drawn from the line.
- **User feedback:** don't invent interaction methods; use the **Android XR / XRI defaults**. Key preview should be off by default.
- **Lesson:** this project had only `com.unity.xr.hands` and no XR Interaction Toolkit. The fix was to add the toolkit, not to write raw joint math.

## Iteration 4: stock XRI hands rig + UGUI panel

- **Change:**
  - Added `com.unity.xr.interaction.toolkit` 3.6.1 and copied the main app's XRI 3.5.1 samples (Starter Assets, Hands Interaction Demo) and the XR Hands 1.8.1 HandVisualizer sample.
  - The build grafts `XR Origin Hands (XR Rig)` onto the AR scene's XR Origin, which has to keep the AR managers.
  - Enabled `HandInteractionProfile` and `MetaHandTrackingAim`, matching the main app.
  - Rewrote the panel as a world-space UGUI canvas: `Slider`s, `<` `>` `Button`s, and Save / Keys / Rescan, with a `TrackedDeviceGraphicRaycaster` and an `XRUIInputModule` event system.
  - Removed the custom ray, joint dots and wrist dot.
  - The key preview starts hidden.
- **User report:** the hand overlay was **bright pink**, showed in one eye only, and glitched.
- **Root cause:**
  - AuraMarkerTest was still on the **Built-in** render pipeline, inherited from Google's ImageTracking sample.
  - The demo hand material `Unity_Hand_Medium` uses `Unity_Hand_Noise.shadergraph`, which targets **URP only**, so Unity drew its magenta error shader.
  - That fallback evidently doesn't handle single-pass stereo, which explains the single eye.
- **Lesson:** check the render pipeline before importing samples. The XRI samples assume URP, and the main app is URP.

## Iteration 5: detours (all reverted)

- **Removing the Hand Visualizer:** wrong. The user wants the default grey hands visible.
- **The main app's depth-only hand occluder** (`PianoTeacher/HandOccluder`, queue 1990, +4 mm inflate), ported to Built-in: not what was asked for. The user wanted the stock grey hand, not occlusion.
- **A Built-in `HandGlove` shader** imitating the grey glove colours: a substitute rather than the stock look.
- **Side effect:** the "restore Hand Visualizer" step instantiated a second rig and moved only its `Hand Visualizer`, then deleted that rig.
  - The visualizer's serialized mesh references (`m_AndroidXRLeftHandMesh`, etc.) pointed into the deleted copy.
  - On the glasses the hands appeared as **two dots stuck in the room**.
- The user did adjust and save in this build: `Saved XYZ in metres: left=(0.0040, 0.0250, 0.0870) right=(0.0110, 0.0250, 0.0760)`. That was a 5 mm Left end nudge.
- User report: the arrows covered the value text.

## Iteration 6: URP (current)

- **Change:**
  - Added `com.unity.render-pipelines.universal` 17.6.0.
  - Copied the main app's `Performance URP Config.asset` and its `Android Preset.asset` renderer. They reference only package scripts.
  - The build sets `GraphicsSettings.defaultRenderPipeline` and clears the per-quality-level overrides.
  - The project's own shaders (`Aura/LineGlow`, `Unlit/Color`, TextMesh) are unlit, so they keep working under URP.
  - Deleted `HandGlove` and `HandOccluder`.
  - The rig graft now **deletes everything under `Camera Offset` except the camera and re-grafts the whole rig every build**, so intra-rig references stay intact.
  - Panel widened to 620 mm. The `>` arrow ends at 184 and the value text starts at 195, so they no longer overlap.
- **Logs:**
  - `Build Finished, Result: Success`; `m_CustomRenderPipeline` points to the URP asset.
  - On the device: `Loaded offsets …`, `Hands subsystems=1 running=True left=True right=True`.
  - The glasses capture shows passthrough plus the cyan HUD, and nothing pink.
- **Pending:**
  - Confirm on the glasses that the stock grey hands track in both eyes.
  - Correct the line, which the user says is still mostly wrong.

## Iteration 7: stricter marker lock, wider sliders (current)

- **User report on iteration 6:**
  - The orange dot on the left marker appears **below** the real marker, and that error carries into the line.
  - The line sits far too low even with the slider at its limit; the range needs to double.
- **Logs:** two back-to-back scans locked the left marker at x = 1.95 m and then 1.77 m, with spans 1.114 m and 1.271 m (about 1.24 m expected). The old lock (12 samples, 0.45 s, 15 mm) accepted biased windows, and the 0.85–1.55 m span gate let a 1.114 m span through.
- **Change:**
  - The lock now needs ≥30 samples over ≥1.2 s, all within 10 mm.
  - The span gate is 1.18–1.32 m (a bad span restarts the scan automatically).
  - The marker dot is drawn exactly at the tracked centre (previously 2 cm above it) at 8 mm.
  - Sliders now cover ±60 mm.
- **Pending:** on-glasses check. If locks are still biased, measure the printed square: any print scaling away from 33.0 mm distorts the pose depth.

## Lessons for the main app

1. **Use the platform defaults.**
   - Stock XRI hands rig, near-far ray with pinch, poke, world-space UGUI, the same as the main app.
   - Do not write custom input. A palm-toward-face pinch is the Android XR system menu.
2. **Match the main app's render pipeline (URP)** before pulling in XRI or XR Hands samples. Built-in turns them magenta and can break stereo.
3. **Graft prefab rigs whole.** Sample rigs carry cross-references between children.
4. **Hand permission timing:** if `HAND_TRACKING` is granted after the session starts, restart `XRHandSubsystem`. Log hand state so "hands don't work" can be told apart from "no data".
5. **Marker poses can be badly biased** when the lock window is short and loose (left marker locks 18 cm apart; spans 1.114–1.271 m). Require a long, tight dwell and gate the span against the known piano width. Per-end adjustment is still needed.
6. **Anchors are unsupported** on this runtime (`XR_ERROR_FUNCTION_UNSUPPORTED`). Expect the line to sit at fixed world positions.
7. **Offsets measured from photos are estimates.** Only values saved on the device count as calibrated. The 25 mm marker-to-key height is still unmeasured.
