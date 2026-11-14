# DEVLOG — running log (newest at top)

Each entry: date/time, who (person or agent), what changed, what works, what's broken or next.

---

## 2026-11-14 ~14:55 — Claude Code (for Ari): visible keyboard + two-hand calibration
- On-device run of the 14:11 build: saved calibration scale 0.548 (points ~66 cm apart) → a stray 2nd pinch ended calibration early.
  (That build predates the span check.)
- **Off keys now drawn faintly** (white 10% grey, black 3–5%) so the whole keyboard is visible on the additive display;
  `upcomingColor` brightened (0.15,0.35,0.6 → 0.25,0.55,1). `SetIdleKeysVisible(bool)` to toggle.
- **Two-hand calibration (default):** left hand on the far-left key, right hand on the far-right key, pinch both within 1 s.
  A lone pinch only shows its marker + a hint. Span check / swap still apply. One-hand flow kept behind `twoHandCalibration = false`.
- Prompt re-wrapped: 29° wide. Verified in Editor Play mode (scripted): lone pinch ignored, pair calibrates at scale 1.000,
  pinches 2 s apart ignored, 30 cm pair rejected.
- **Glasses screenshots work:** `adb shell screencap -d <XREAL display id> -p` (display "XREAL 1 Ultra") captures the composited
  passthrough + app view, so the overlay can be checked from the PC.

## 2026-11-14 ~14:45 — Claude Code (for Ari): calibration robustness (tiny keyboard), geometry assessment
- **Tiny keyboard root cause:** a bad calibration was accepted and saved (PlayerPrefs on the puck: scale 0.0497, yaw ~174°, i.e. the two
  pinches were ~6 cm apart, right before left). Geometry was not the problem.
- **Assessment (kept procedural overlay + 2-point calibration):** model octave 164.5 mm / 52 whites 1,222 mm matches the modern standard;
  Yamaha P-45 (P71 equivalent) is 1,326 mm wide overall. Key-level P71 dims aren't published; a tape-measure check is still TODO.
  A 3D model adds no alignment or mapping value on an optical see-through display. A 3rd point / anchors are postponed until testing shows need.
- **Changes:** span sanity check (60–160% of `ExpectedSpanMeters()`, ~1.20 m) with an explanation and restart; right-then-left swap;
  overlay always starts at real size; clearer numbered instructions + "Calibrated!" message; prompt text 0.25 → 0.135 (61° → 35° wide);
  keep tilt along the keyboard (`maxTiltDegrees` 5); lower pinch points by `pinchHeightOffset` 0.008 m; preview clears only its own keys.
- **Verified in Editor Play mode (scripted):** 6 cm span rejected; right-then-left + 1° tilt → swapped, scale 1.000, tilt 1.00°, A0/C8
  within 0.1 mm of the points; lesson key D4 survives the preview clear. **Not yet tested on the glasses.**

## 2026-11-14 ~14:20 — Claude Code (for Ari): falling UI fixed, Direct Preview (Play mode) set up
- **"Falling text" root cause:** the template rig's `GravityProvider` (+ CharacterController, move/turn/teleport/climb/jump) was active and
  `Dev_Ari` has no floor, so the whole rig fell. Disabled the rig's `Locomotion` child and CharacterController in `Dev_Ari` (prefab untouched);
  the scene builder does the same.
- **Direct Preview:** added `com.google.xr.extensions` 1.5.0 (local tarball in `Packages/`, deps already satisfied, no version bumps).
  Windows graphics API → Vulkan only. OpenXR Windows tab: Android XR Support, AR Session, Hand Interaction Profile, Extensions Session +
  Streaming, Depth 24-bit. Windows + Android OpenXR validation: 0 issues. Android build settings unchanged.
- Still needed per machine: Android XR Engine Hub (Install Stream Client, Set for Active OpenXR Runtime). Not tested yet.
- Note: Unity 6.6 isn't writing `%LOCALAPPDATA%/Unity/Editor/Editor.log` here (stale since 13:16); use the Console / MCP `read_console`.

## 2026-11-14 ~14:15 — Claude Code (for Ari): first on-device runs (Aura), passthrough + calibration fixes
- **World was blacked out:** logcat showed `XR_ENVIRONMENT_BLEND_MODE_OPAQUE` (100% lens dimming). Fix per the Android XR package docs:
  enabled OpenXR **Android XR: Session** + **Android XR: Camera (Passthrough)**, added an `AR Session` and an `ARCameraManager` on the
  Main Camera (background already solid (0,0,0,0)). Verified on device: `OPAQUE → ALPHA_BLEND`, dimming 0.9 → 0.1. The scene builder adds these too.
- **Prompt/overlay not visible:** placed in `Start()`, but the XR session became visible ~2 min later and the runtime re-centers
  (`ReferenceSpaceChangePending`). Now: wait for a tracked, settled head pose, place the overlay ~45 cm ahead / 35 cm down, re-place
  on `XRInputSubsystem.trackingOriginUpdated`. Prompt stays still while in view and only re-centers after being out of view (no jitter).
- **System-menu pinch conflict:** calibration counted every pinch, including the Android XR system gesture (palm toward you).
  Now ignores pinches when `MetaAimFlags.SystemGesture` is set and requires palm-down (`requirePalmDown`, `palmDownThreshold`).
- **Key preview:** lowest/highest keys pulse while calibrating; all C keys light for 5 s after calibrating (alignment check).
- Known noise in logcat: `AssetPackManager` ClassNotFound (Play Asset Delivery, harmless), `SCENE_UNDERSTANDING_COARSE` not granted (light estimation only).
- **Next:** confirm on device that the prompt is steady and the system menu works; tune `pinchStartDistance` / `palmDownThreshold`.

## 2026-11-14 ~14:00 — Claude Code (for Ari): Android XR build settings (shared project settings)
- **OpenXR (Android tab):** `Android XR Support` was **OFF** and `Meta Quest Support` was **ON**, so the project was configured for Quest, not Android XR.
  Changes: Android XR Support **on**, Hand Interaction Profile **on**, Meta Quest Support **off**, Meta Quest Touch Plus/Pro controller profiles **off**,
  Meta `Display Utilities` **off** (the Android XR one stays on). Hand Tracking Subsystem + Meta Hand Tracking Aim were already on.
- **Build scenes:** `Dev_Ari.unity` enabled at index 0. `SampleScene` is kept in the list but disabled. Swap to `Main.unity` when it exists.
- **Already correct (matches Google's Android XR Unity setup guide):** Vulkan only, IL2CPP + ARM64, min API 34, GameActivity, Resizeable Activity, URP HDR off.
- OpenXR Project Validation: 0 issues. XR validation: 1 warning (XRI Hands Demo wants the XR Hands HandVisualizer sample re-imported at 1.9.0). Not blocking, left alone.
- `ProjectSettings/AndroidXRSettingsManager.asset` `hasInitializedXRSettings` flipped 1 → 0 on its own (package first-run flag), not from these edits.
- **To build:** File → Build Profiles → Android → Run Device = the puck → Build And Run. **Not built or run on device yet.**

## 2026-11-14 ~13:20 — Claude Code (for Ari): Unity MCP
- Added MCP for Unity (`com.coplaydev.unity-mcp`, pinned `#v10.0.0`) to `Packages/manifest.json`. No other package versions changed.
- **Removed Unity AI Assistant** (`com.unity.ai.assistant` manifest entry and embedded `Packages/` folder) because it conflicts with MCP for Unity
  on Unity 6.3+ (`System.Collections.Immutable`).
- Installed `uv` 0.13.8 (user scope) on Ari's machine. Python 3.13.15 was already there.
- **Works:** `claude mcp list` → `UnityMCP: http://127.0.0.1:8080/mcp (HTTP) √ Connected`. No compile errors in Editor.log after the import.
- **Verified through MCP (read-only):** active scene `MIDI OVERLAY` lists its 5 root objects (Directional Light, XR Origin (XR Rig),
  XR Interaction Manager, EventSystem, Plane). Console has 0 errors and 0 warnings. Setup steps are in `CLAUDE.md` under "Unity MCP setup".

## 2026-11-14 ~13:05 — Claude Code (for Ari)
- Moved `PianoOverlay.cs` into `Assets/Scripts/UI/` and `INTERFACES.md` / `DEVLOG.md` into `docs/`.
- Added `PianoCalibrator.cs`: two hand-tracking pinches (XR Hands) → `CalibrateFromTwoKeys`, pinch markers, floating prompt, `Recalibrate()`.
- `PianoOverlay`: `SaveCalibration()` is now public; key colliders are removed immediately (`DestroyImmediate`) so keys never have colliders.
- Added `Editor/DevAriSceneBuilder.cs` (menu **Tools ▸ Piano ▸ Build Dev_Ari Scene**): builds `Assets/Scenes/Dev_Ari.unity` with the project's
  hands rig (VR Template "Complete XR Origin Set Up Hands Variant" + Hands Permissions Manager), PianoOverlay with URP Unlit material
  (`Assets/Scripts/UI/Materials/`), an XRI grab handle (position-only, handle collider only), a Recalibrate button, and the calibrator.
- Project state found: XRI 3.6.1, XR Hands 1.9.0, OpenXR 1.18.0, Android XR (OpenXR) 1.4.0, Input System 1.20.1, URP 17.6.0.
  OpenXR Android has Hand Tracking + Hand Interaction Profile on. XRI/XR Hands samples in `Assets/Samples` are older (3.5.1 / 1.8.1) than the packages.
  Project-wide input actions setting points at an asset that isn't in the repo (XRI uses its own Default Input Actions, so not blocking).
- **Built** `Assets/Scenes/Dev_Ari.unity` via the menu (through Unity MCP): roots = XR Interaction Manager, Complete XR Origin Set Up Hands
  Variant, Hands Permissions Manager, PianoOverlay (PianoOverlay + kinematic Rigidbody + XRGrabInteractable whose only collider is
  `GrabHandle`), PianoCalibrator (all references wired). Compiles with 0 errors / 0 warnings in the Console.
- **Untested:** play mode, and the Aura itself (pinch thresholds, pose space, colors). **Next:** press Play and try `C`/`R`/`1`/`2`/`3`/`0`,
  then test calibration on device and tune `pinchStartDistance`.

## 2026-11-14 ~13:00 — Ari
- Added `PianoOverlay.cs` (88-key procedural overlay for the Yamaha P71, KeyState API, two-pinch calibration, editor debug keys).
- **Works:** builds in the Editor; debug keys 1/2/3/0.
- **Next:** hook the hand-tracking pinch to `CalibrateFromTwoKeys`, add a grab handle for fine adjustment, test colors on the Aura.
- **Blocked on:** MIDI input (to drive keys from the real piano).
