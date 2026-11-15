# CLAUDE.md — AR Piano Teacher (Immerse the Bay 2026)

Read this first. Then read `docs/INTERFACES.md` before touching any code another person owns.

## What we're building
An AR piano teacher on **XREAL Aura** (Android XR, optical see-through glasses). The player sits at a real
**Yamaha P71** digital piano. The glasses overlay which key to press, show the sheet music on a floating panel,
and an AI coach gives short spoken feedback between phrases.

- **Main demo:** Freeplay of the 35-note right-hand Für Elise opening (8 bars). A separate CurriculumEngine exists but is not in Main.
- **Core loop ("wait mode"):** highlight the next key → wait for the correct press → advance → AI comments after the completed excerpt.
- **Pitch:** see-through glasses show your real keys and fingers sharply (passthrough headsets can't), plus a coach that talks to you,
  plus linking each written note on the sheet to its physical key.

## Hard deadline and submission rules
- **Pens down: Sunday Nov 15, 9:00 AM PT.** Submissions are downloaded at exactly 9:00.
- Devpost + **public Git repo** + **30-second vertical demo video**.
- **All team code is MIT-licensed** (`LICENSE` at repo root).
- **Never commit API keys or secrets.** The repo is public. Keys go in gitignored files only. Check `git status` before every commit.
- **AI keys:** copy `Assets/StreamingAssets/secrets.example.json` to `secrets.json` (gitignored) and fill in
  `ANTHROPIC_API_KEY` / `GROQ_API_KEY`. Files in `StreamingAssets` are **packed inside the APK**, so every build carries the
  keys: never upload or share an APK built with real keys.

## Stack
- **Unity 6000.6.8f1** (everyone must use exactly this version). Fallback: 6000.3 LTS if Android XR breaks.
- Android build target, OpenXR + Android XR packages, XR Interaction Toolkit, XR Hands (hand tracking).
- Input System package only (no legacy `UnityEngine.Input`).
- **Everything runs on the Aura.** No laptop or server in the loop.

## Architecture
```
Yamaha P71 ──USB MIDI──► Aura puck ──► Unity app
                                        ├─ MidiPianoReader   (native Android MidiManager bridge → C# events)
                                        ├─ AppFlow          (startup, calibration, overlay and coach wiring)
                                        ├─ PerformanceEvaluator (wait mode, scoring, RunStats — no AI)
                                        ├─ UI
                                        │   ├─ PianoOverlay  (88-key highlight overlay + calibration)
                                        │   ├─ SongNoteDisplay (sheet music lines + current-measure highlight)
                                        │   └─ CoachOrb      (AI coach visual: Idle/Listening/Thinking/Speaking)
                                        └─ AICoach           (LLM + speech in/out, runs BETWEEN phrases only)
```

**Rule: the AI is never in the real-time loop.** Note matching, highlighting and advancing must work instantly
even if the AI is slow or offline. The AI only receives a short JSON summary of performance and speaks between phrases.

**Rule: the AI does not generate notes.** Main's notes come from `SongLibrary.FurElise`; the separate curriculum uses
JSON song resources. `Assets/Music/` holds the sheet images. Main allows spoken feedback only, not demo/fingering actions.

**Lesson progression** (measure by measure, tempo ladder, full play-throughs, repairing weak measures) follows
`docs/COACH_PLAYBOOK.md`. Read it before working on the LessonEngine or the coach's actions.

## Ownership (update when this changes)
| Area | Owner | Folder |
|---|---|---|
| UI: PianoOverlay, PianoCalibrator (+ hand occlusion), SheetPanel, CoachOrb | Ari | `Assets/Scripts/UI/` |
| MIDI input (`MidiPianoReader`, `MidiLogUI`, Android `MidiBridge.java`) | Jordan | `Assets/Scripts/` (root, not yet in `Midi/`), `Assets/Plugins/Android/` |
| PerformanceEvaluator / SongNoteDisplay (wait mode, scoring, `RunStats`) and AppFlow | Jordan | `Assets/Scripts/Lesson/`, `Assets/Scripts/App/` |
| AICoach (LLM, speech in/out) | Canady | `Assets/Scripts/AI/` |

Don't edit another owner's folder without telling them. If you need something from their module,
add a request to `docs/INTERFACES.md` under "Requested".

## Conventions
- **One scene per person** for development (`Assets/Scenes/Dev_<Name>.unity`). Only `Assets/Scenes/Main.unity`
  is shared, and only one person edits it at a time, announced in chat. Unity scene merges are painful.
  `Main` exists and is the only enabled build scene (index 0). Dev scenes: `Dev_Ari` (overlay + calibration),
  `JordanScene` / `JordanPianoReading` (MIDI), `AITest` (AI coach). Keep AIDebugPanel out of Main: its keys clash with the evaluator.
- Communicate between modules through the public methods and events listed in `docs/INTERFACES.md`, not by
  reaching into another module's internals.
- Notes are **MIDI note numbers** everywhere (middle C = 60, P71 range = 21 to 108).
- Units are meters.
- Every module gets an Editor-only debug driver (keyboard shortcuts) so it can be tested without the piano or headset.

## Main demo flow and verification
- Open `Assets/Scenes/Main.unity`. `AppFlow` finds its references automatically; the evaluator starts inactive,
  and `PianoCalibrator.calibrateOnStart` is off.
- Android: request microphone permission **before the intro** and wait for grant, denial, or dismissal. With permission
  already granted there is no dialog. Denial/dismissal skips recording without asking again during the intro flow;
  calibration and the piano lesson still work. The Editor skips this Android permission step.
- Intro speech → listen for 4 seconds (when permitted) → four-point calibration → lesson. Any answer or silence continues.
  Pinch FAR then NEAR end of A0, then FAR then NEAR end of C8. Sheet placement is 0.45 m above / 0.20 m behind the drawn
  keyboard center; it updates after recalibration without restarting the lesson.
- Next key lights up; correct/wrong presses flash. At the end of all 35 notes, the coach receives `RunStats` and speaks
  feedback via the orb. After the restart delay (1.5 s), play the first E5 again for a new attempt.
- Editor keys: **Enter** skips speech/listen waits; **C** simulates calibration, **R** recalibrates;
  **Space** correct note, **X** wrong note, **P** toggles autoplay, **Backspace** restarts. Use P to verify completed-run coaching.
- Missing API keys: canned coach feedback + native TTS on Android (silent in Editor); transcription requires a Groq key.
  Native TTS needs a working voice on the puck. Never share an APK containing real keys.
- Full device checklist and adb commands: `docs/AURA_MAIN_TEST.md`. Editor tests do not validate the Android dialog,
  real MIDI, alignment, hand gestures, or optical-display readability.

## Designing for the Aura (optical see-through)
- The display is **additive**: it only adds light. **Black = invisible.** There are no dark glass panels.
  Use bright rims, glow and high contrast.
- Sheet music should be **light notes on a transparent background**, or raise the lens dimming while the panel is visible.
- Black is invisible **in the lenses**, but it still writes alpha 1: screen captures and recordings (the demo video) show it as
  solid black. Don't draw black fills; disable the renderer instead.
- Any material override on the template hand meshes must replace **every** slot: they carry `DepthOnly` plus a grey glove
  material (`Unity_Hand_Medium`), and leaving the glove draws an outline around the hands.
- Test on the real glasses early. Colors that look good in the Editor often wash out.

## Building for the Aura (Android XR) — settings that must stay this way
- Deploy target is the **puck** (the Android device), over USB/adb. Build Profiles → Android → Run Device = puck → Build And Run.
- OpenXR **Android** tab: **Android XR Support** on, **Meta Quest Support** off, **Hand Tracking Subsystem** + **Meta Hand Tracking Aim**
  + **Hand Interaction Profile** on, **Android XR: Session** + **Android XR: Camera (Passthrough)** on.
- **Passthrough:** without an `ARSession` in the scene and an enabled `ARCameraManager` on the Main Camera, Android XR runs in
  **OPAQUE** blend mode = 100% lens dimming = the room is blacked out. Camera background must be Solid Color (0,0,0,0).
- **Seated AR:** disable the template rig's `Locomotion` child and its `CharacterController`. With `GravityProvider` on and no floor
  collider the whole rig falls and world-placed UI drifts away.
- Head-relative UI must wait for a tracked head pose: the XR session can start long after `Start()` and the runtime re-centers.
- Don't treat every pinch as input: palm-toward-you pinch is the Android XR system menu (`MetaAimFlags.SystemGesture`).

## Play mode on the Aura (Direct Preview)
- Uses Google's **Android XR Engine Hub** (Windows) + **Android XR Extensions for Unity** (`com.google.xr.extensions`, local tarball
  `Packages/com.google.xr.extensions.tgz`, v1.5.0 from github.com/android/android-xr-unity-package releases; "unsigned package"
  warning on import is expected).
- Project side (done): Windows graphics API **Vulkan only**; OpenXR **Windows** tab: Android XR Support, Android XR: AR Session,
  Hand Interaction Profile, Android XR (Extensions) **Session** + **Streaming**; Render Mode Multi-pass, Depth 24-bit, Foveation Legacy.
- Per machine: install the Engine Hub, close Unity, connect the puck, **Install Stream Client**, **Set for Active OpenXR Runtime**
  (this replaces e.g. Meta Horizon/Quest Link as the PC runtime). On laptops, set `Unity.exe` to the discrete GPU (Vulkan video encode).
- Then press Play in Unity; the stream starts on the glasses. Audio plays on the PC, not the glasses.

## Known Aura / Unity gotchas (from the June Aura hackathon)
- Streaming to the device crashes → switch the Editor graphics API from Vulkan to DX11. **(Outdated?)** Google's current Direct Preview
  guide requires Vulkan for Windows; we follow that now. If Play-mode streaming crashes, revisit this.
- Direct Preview is flaky → restart Unity, the hub and the device, or use the XR Simulator for in-editor testing.
- Play mode crashes with the Android XR Extensions package installed → remove it if it's not needed.
- Prerelease and retail firmware images aren't interchangeable. Don't reflash without checking.

## Device tooling (puck)
- **See what the glasses show:** `adb shell screencap -d 4626964206039778834 -p /sdcard/x.png` then `adb pull /sdcard/x.png`
  (display "XREAL 1 Ultra" = camera image + app content). Plain `adb exec-out screencap` fails: the puck has two displays.
- **Wireless adb:** put the PC and puck on a private network (phone hotspot, not the open venue Wi-Fi), then with USB plugged in
  `adb tcpip 5555`, unplug, `adb connect <puck-ip>:5555`. Reverts to USB-only on reboot or `adb usb`.
- **Logs:** `adb logcat -s Unity`; calibration lines are tagged `[PianoCalibrator]`. App files (e.g. logs written by a script)
  are under `/sdcard/Android/data/com.DefaultCompany.VRTemplate/files/`.
- **Aura runtime facts (Android XR 1.0, probed 2026-11-14):** marker / QR / planar-image tracking, depth texture, raycast,
  scene meshing and `XR_ANDROID_hand_mesh` / `XR_FB_hand_tracking_mesh` are listed; Unity scripts get **no camera CPU frames**.

## Unity MCP setup (lets agents read/drive the Unity Editor)
- **Package:** `com.coplaydev.unity-mcp` pinned in `Packages/manifest.json` to
  `https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.0.0`. Don't take the in-window "Update available" prompt
  on your own; bump the pin for the whole team at once.
- **Prereqs:** Python 3.10+ and `uv` on PATH. Windows install of uv (no execution-policy change needed):
  `powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"`. Restart Unity afterwards so it sees the new PATH.
- **Steps:**
  1. Open the project and let it import.
  2. **Window → MCP for Unity → Toggle MCP Window**. Click **Configure All Detected Clients** (registers `UnityMCP` with Claude Code, local scope).
  3. Transport **HTTP Local**, URL `http://127.0.0.1:8080`. Click **Start Server**. The session dot should turn green.
  4. Check from a terminal: `claude mcp list` should show `UnityMCP ... √ Connected`. Then restart Claude Code from the project root.
  5. If it isn't registered: `claude mcp add --transport http UnityMCP http://localhost:8080/mcp`.
- **Port:** 8080 by default. If it's taken, change it to 8090 in the MCP window and re-register with that URL.
- **The server is not started automatically.** After every Unity launch, click Start Server again or agents get `ECONNREFUSED`.
- **Tools missing in Claude Code?** They only load in a session started *after* the server is running. `claude mcp list` can show
  Connected while the current session has no UnityMCP tools; restart Claude Code from the project root.
- **Conflict: Unity AI Assistant (`com.unity.ai.assistant`).** On Unity 6.3+ it clashes with MCP for Unity over `System.Collections.Immutable`.
  We **removed** it (manifest entry and the embedded `Packages/com.unity.ai.assistant` folder). If you need it back, also add
  `System.Collections.Immutable` v9.0.0 as a DLL in `Assets/Plugins/`. Its `relay_win.exe` can keep running after Unity closes and
  lock the folder; end that process before deleting it.

## Agent workflow (Claude Code, Cursor, etc.)
1. Read `CLAUDE.md` and `docs/INTERFACES.md`.
2. Stay inside the folder(s) of the area you were asked to work on.
3. **When you add or change any public method, event, enum or prefab other modules use: update `docs/INTERFACES.md`
   in the same commit.**
4. **Append a dated entry to `docs/DEVLOG.md`** saying what changed, what works, and what's broken or next.
5. Keep commits small with clear messages. Never commit secrets.
