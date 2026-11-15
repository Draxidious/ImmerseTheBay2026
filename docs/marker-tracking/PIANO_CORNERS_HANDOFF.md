# Aura Piano Corners handoff

Current state as of 2026-11-14, about 23:35 PT. For how we got here, see `PIANO_CORNERS_ITERATIONS.md`.

## Purpose and scope

Two printed markers on the Yamaha P71 give a one-time line along the **back edge of the white keys**, from the outer (left) edge of A0 to the outer (right) edge of C8. That is where the white keys disappear under the panel. The user can fine-tune the line with a standard world-space UI panel and save the offsets. An optional 88-key preview is drawn from the line so alignment can be judged against the real keys.

This is a prototype in `C:/Users/Ari/Documents/Projects/AuraMarkerTest`, which is not a git repository. The main piano project is `C:/Users/Ari/Documents/Projects/ImmerseTheBay2026`.

The mounted paper targets are **ArUco 4x4_50**: ID 0 on the left (A0 end) and ID 1 on the right (C8 end). Each black symbol is 33 mm wide when printed at Actual Size. The 42 mm cutouts include the white margin. Print sheet: `ImmerseTheBay2026/output/pdf/Yamaha-P71-Corner-Markers-Letter.pdf`. The QR pair on the same sheet is a different target format and is not used.

## Stack (matches the main app)

| Piece | Version / setting |
|---|---|
| Unity | 6000.6.8f1, Android, IL2CPP ARM64, Vulkan, min API 34 |
| Render pipeline | **URP 17.6.0**, using the main app's `Performance URP Config` + `Android Preset` renderer (copied to `Assets/Settings/`) |
| XR Interaction Toolkit | 3.6.1, with the main app's 3.5.1 samples (Starter Assets, Hands Interaction Demo) |
| XR Hands | 1.9.0, with the main app's 1.8.1 HandVisualizer sample |
| AR Foundation / Android XR | ARF 6.6.2, `com.unity.xr.androidxr-openxr` 1.4.0, OpenXR 1.18.0 |
| Google XR Extensions | 1.3.1, local `Packages/planar-image-extensions.tgz` (kept for the planar-image diagnostic) |

The build enables these OpenXR Android features: Google `XRSessionFeature` and `XRMarkerTrackingFeature` (ArUco); Unity Android XR Session, Camera, Support and `ARAnchorFeature`; `HandTracking`; `HandInteractionProfile`; `MetaHandTrackingAim`. All other features are switched off.

## Build and launch

- Entry point: `BuildRuneTest.RunPianoCorners` in `Assets/Editor/BuildRuneTest.cs`. It does all of the following every time:
  - sets URP as the default pipeline and clears per-quality overrides;
  - sets the OpenXR features above;
  - writes the ArUco reference library;
  - configures `RuneLine` (offsets, materials, lift);
  - re-grafts the XRI hands rig (see below);
  - saves `Assets/Runes/PianoCorners.unity` and builds.
- Command line (the project must not be open in another Editor):
  `Unity.exe -batchmode -nographics -quit -projectPath <AuraMarkerTest> -buildTarget Android -executeMethod BuildRuneTest.RunPianoCorners -logFile <log>`
- APK: `Builds/AuraPianoCorners.apk`. Package: `com.ari.aurapianocorners`. Test puck serial: `V1L264B118612B`.
- Install: `adb -s V1L264B118612B install -r Builds/AuraPianoCorners.apk`
- Launch: `adb -s V1L264B118612B shell am start -W -n com.ari.aurapianocorners/com.unity3d.player.UnityPlayerGameActivity`. The explicit component avoids intent-resolution problems on the puck. If the glasses aren't being worn, the app goes straight to the background, so relaunch it while wearing them.
- Grant scene understanding and hand tracking when prompted. If the hand permission is granted mid-run, `RuneLine` restarts the hand subsystem (`Restarted 1 hand subsystem(s) after permission grant`).

### XRI hands rig graft

The scene comes from Google's ImageTracking sample. Its XR Origin carries the AR managers (`ARTrackedImageManager`, `ARAnchorManager`, the sample's `ImageTrackingController`, `RuneLine`), so the origin must stay. Each build does this:

1. Deletes everything under that origin's `Camera Offset` except `Main Camera`. These are the previously grafted rig parts.
2. Instantiates and unpacks the stock `XR Origin Hands (XR Rig)` prefab from the Hands Interaction Demo.
3. Moves all its `Camera Offset` children (Left/Right Hand, Hand Visualizer, Gaze Interactor, controllers) into the AR origin's `Camera Offset`, in one go.
4. Copies the root components `InputActionManager`, `XRInputModalityManager` and `XRGazeAssistance`.
5. Leaves out locomotion, gravity, body transformer and `CharacterController`, because this is seated AR.
6. Adds an `XR Interaction Manager` and an `EventSystem` with `XRUIInputModule` if they are missing.

The rig parts reference each other (for example, `Hand Visualizer` points at the hand meshes under Left/Right Hand). So never move or restore one part on its own: re-graft the whole set. See iteration 6.

## Calibration flow and controls

1. **Look at ID 0, then ID 1.** Hold each one steadily in view. Each target needs ≥30 samples over ≥1.2 s that all agree within 10 mm. A small orange dot (8 mm) marks the locked left target **exactly at the tracked marker centre**, so you can see any tracking error. If the marker span is outside 1.18–1.32 m (the real markers are about 1.24 m apart), the scan restarts by itself.
2. **Geometry.** The left→right axis is the marker baseline projected onto the horizontal plane. Toward-player = world up × axis. This ignores the tilt of the printed target faces.
3. **Offsets** are metres in the order **inward, down, toward player**, measured from each marker centre to that end of the line:
   - Build defaults: left `(0.009, 0.025, 0.088)`, right `(0.010, 0.025, 0.076)`.
   - Saved on the puck at 23:26: left `(0.004, 0.025, 0.087)`, right `(0.011, 0.025, 0.076)`. The user says the line is still mostly wrong; see open checks.
   - A saved `piano-corner-offsets.json` overrides the defaults on launch (`Loaded offsets: ...`).
4. **Line rendering:**
   - 1.5–2.5 mm pulsing core, 3.5–5.5 mm halo, 1.5 mm above the key surface.
   - Each end has a thin tick across the key (2 mm × 18 mm) that marks exactly where the line starts and stops.
   - Image tracking stops once both ends lock.
   - The app tries a native anchor. On the puck this fails (`XR_ERROR_FUNCTION_UNSUPPORTED`), so the line stays at fixed world positions.
5. **Adjustment panel.** A world-space UGUI canvas floats 24 cm toward the player and 14 cm above the marker midpoint, tilted 35° toward the eyes. It works with the stock XRI hand input: point and pinch with the near-far ray, or poke. Rows:

   | Row | Slider right / `>` moves… | Offset |
   |---|---|---|
   | Left depth | left end toward player | `left.z` |
   | Right depth | right end toward player | `right.z` |
   | Height | both ends up | `-left.y`, `-right.y` |
   | Left end | left end right (inward) | `left.x` |
   | Right end | right end right (outward) | `-right.x` |

   Each slider covers ±60 mm around the value at lock time, and `<` `>` step it by 1 mm. The value text shows the change in millimetres. Buttons: **Save** writes the offsets, **Keys** toggles the 88-key preview (**off by default**), **Rescan** clears everything and starts over.
6. **88-key preview** (the Keys button), drawn from the line:
   - White-key width = line length / 52; white keys are drawn 146 mm long.
   - Black keys: 12.5 mm wide, 94 mm long, raised 12 mm.
   - Black-key positions follow the standard layout: C–E split into 5 equal slots over 3 white keys, and F–B into 7 slots over 4 white keys.
   - It moves with every slider change.
7. **Saved data:** `/sdcard/Android/data/com.ari.aurapianocorners/files/piano-corner-offsets.json`. Recalibrating still needs both markers to establish the current session pose.

**Hands.** The stock XRI hand visuals (URP `Unity_Hand_Medium` grey glove) are what you see, with the stock interactors. The script draws no hands of its own and reads no input. It only reads `XRHandSubsystem` for the HUD line `Hands: L ok R ok` and the log. Avoid pinching with the palm facing your face: on Android XR that opens the system menu, which takes focus from the app.

## Where the default offsets came from

Taken from overhead phone photos, using the 23.5 mm white-key pitch as the scale and correcting for parallax because the markers sit about 25 mm higher than the keys. The close-ups agree with the overview photo to within a few millimetres:

| | inward | toward player |
|---|---|---|
| Left (photo 7 close-up / overview) | ≈9 mm / ≈4 mm | ≈87 mm / ≈95 mm |
| Right (photo 1 close-up / overview) | ≈9 mm / – | ≈75 mm / ≈75 mm |

The right marker sits about 12 mm closer to the keys than the left, so the two ends really do differ. The **25 mm height (marker surface → key top) is an estimate** and has not been measured.

## Evidence (puck logs)

- **Marker spans** across runs: 1.260, 1.231, 1.240, 1.269 m. The key span drawn from the line was 1.22 m (front-edge offsets) and 1.250 m (back-edge defaults). The expected value is 52 × 23.5 mm ≈ 1.222 m, so the tracked span varies by about ±2–3 %.
- **Anchors:** `xrCreateAnchorSpaceANDROID` and `xrCreateDeviceAnchorPersistenceANDROID` both return `XR_ERROR_FUNCTION_UNSUPPORTED` on this runtime.
- **Hands:** with permission granted at launch, the log shows `subsystems=1 running=True` and both hands tracked. Tracking drops in and out as the hands leave the cameras' view.
- **Saving works:** `Saved XYZ in metres: left=(0.0040, 0.0250, 0.0870) right=(0.0110, 0.0250, 0.0760)`, and the next launch logged `Loaded offsets` with the same values.
- **URP build** (23:32): starts cleanly, passthrough is visible, and the HUD renders. Glasses capture: `adb shell screencap -d 4626964206039778834`.

## Open checks

- **Marker pose scatter.** Two back-to-back scans at 23:32 locked the left marker at x = 1.95 m and then 1.77 m (spans 1.114 m and 1.271 m), and the user saw the left dot appear below the real marker. The 23:36 build tightened the lock (30 samples / 1.2 s / 10 mm) and the span gate (1.18–1.32 m). It hasn't been tried on the glasses yet. If locks are still off, measure the printed black square: anything other than 33.0 mm (for example "fit to page" scaling) skews the depth and span estimates.

- **Line position is still mostly wrong** according to the user, even after saving. Next: get the exact direction and size of the error (or a glasses capture with Keys on) and either adjust with the panel or remeasure the 25 mm height.
- The **stock grey hands under URP** have not yet been seen on the glasses. Confirm they follow the hands, appear in both eyes, and aren't too bright.
- Key-span scale error of about 2–3 % from marker tracking. If the preview drifts toward one end, use Left end / Right end.
- `PianoCorners.unity` still holds a stale serialized field `handMaterial` on `RuneLine`. It's harmless and drops on the next Editor save.
- Startup logs `The referenced script on this Behaviour (Game Object 'Directional Light') is missing!` from the original sample scene. Harmless.

## Read out the result

```powershell
adb -s V1L264B118612B logcat -d -s Unity | Select-String '\[RuneLine\]'
adb -s V1L264B118612B pull /sdcard/Android/data/com.ari.aurapianocorners/files/piano-corner-offsets.json .
adb -s V1L264B118612B shell screencap -d 4626964206039778834 -p /sdcard/x.png; adb -s V1L264B118612B pull /sdcard/x.png .
```

Useful `[RuneLine]` lines:
- `Left locked`, `Marker span=`, `Right locked; key span=` — scanning.
- `Hands subsystems=… left=… right=…` — hand tracking state.
- `<row> ±N mm; left=… right=…` — arrow steps.
- `Key preview on/off` — the Keys button.
- `Saved XYZ in metres` — offsets saved.
- `Loaded offsets` — saved offsets read at launch.

Only `Saved XYZ` values are confirmed by the user on the device. The photo-derived defaults are estimates.
