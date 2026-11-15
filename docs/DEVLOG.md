# DEVLOG — running log (newest at top)

Each entry: date/time, who (person or agent), what changed, what works, what's broken or next.

---

## 2026-11-14 ~21:30 — Claude Code (for Nehemiah): sheet-music PNGs for every song in the DB
- **Pipeline:** `Assets/Scripts/Songs/Editor/MusicXmlExporter.cs` (DB → MusicXML: pickup as implicit measure, ties across bar lines
  and into printable lengths, chords, extra voices for overlapping notes, rests, key-aware accidentals, beams per beat group,
  fingering, per-bar clef changes, title/composer/tempo/arrangement credits, note ids = DB note index) →
  `Tools/SheetMusic/render_sheets.py` (Verovio 6.4.1 → SVG, text-size fix, resvg-py 0.5.0 → PNG + bar/notehead rects) →
  `SheetMusicMenu` (Tools ▸ Songs ▸ Generate Sheet Music) maps everything back to the DB and writes `layout.json`.
  Runtime loader `SheetLibrary`. Added `keyFifths`/`keyMode` to SongEntry + all song JSONs (Clair de Lune −5 = D-flat major, flats).
- **Python:** private venv `Tools/SheetMusic/.venv` (gitignored) with only verovio + resvg-py (`uv`, no admin, nothing system-wide).
  PyMuPDF was tried first and removed (renders Verovio SVG blank).
- **Output:** 7 songs, 10 pages (Clair de Lune 4). Every song: all bars present, every DB note placed, no pitch mismatches
  (checked automatically each run). Overlay check on Für Elise: all bar/notehead rects land exactly on the engraving.
  Looked at every song's page: correct key/time signatures, beams, accidentals, rests, ties, fingering (Hot Cross Buns), clef change
  in Für Elise bars 14–15.
- **Fixed along the way:** metronome glyph rendered as boxes (music font missing in resvg) → tempo as header text; Verovio's
  0px text broke titles/bar numbers/fingering in resvg → patched; OneDrive locked the temp folder → per-run folders; a texture
  AssetPostprocessor made Unity reimport every texture (one-time) → replaced by per-file import settings; uncompressed pages were
  ~24 MB each → ASTC 4x4 on Android (~6 MB).
- **Known limits:** Clair de Lune's automatic simplification puts high LH notes on ledger lines in bars ~15–24 (needs a hand edit);
  Verovio prints the first system's bar number "1" above the Für Elise pickup. Images are black on white (asked for) — invert for the Aura.

## 2026-11-14 ~20:55 — Claude Code (for Nehemiah): Für Elise bars 9–22 + Clair de Lune from public-domain MIDI
- **Sources:** Mutopia Project, both "Public Domain / CC: No rights reserved": Für Elise WoO 59 (piece 931, Breitkopf & Härtel 1888) and
  Clair de Lune (piece 1778, E. Fromont 1905). Downloaded only the .mid files into an empty scratch folder; checked: `MThd` header,
  Standard MIDI format 1, 3 tracks, 7.6 KB / 12.5 KB, read only by our own parser (nothing executed). Kept in `Assets/Music/` with
  SHA-256 + license in `Assets/Music/SOURCES.md`.
- **Für Elise:** the MIDI doesn't unfold the repeat (1st ending is 2 eighths, then the 2nd ending), so the bar grid after bar 8 was
  re-aligned (bar 9 = tick 4608). Bars 9–22 (94 notes) added to `fur_elise.json` as explicit notes; bars 1–8 stay typed.
  Check: bars 17–22 (imported) = typed bars 2–7, 41/41 notes. Sections: 1–8, 9–22, 1–22.
- **Clair de Lune:** imported with Tools ▸ Songs ▸ Import MIDI Files (simplified): 977 notes, 72 bars, 9/8, 120 eighth-bpm. Sections:
  1–8, 1–14, 1–72. Names print with sharps (D-flat major shows as C#/G#) — SongFormatter limitation.
- All 7 songs validate: 0 errors, 0 warnings. Still `verified: false` (simplification and hand = staff are automatic).

## 2026-11-14 ~20:40 — Claude Code (for Nehemiah): song database (sheet music → MIDI notes) for the AI coach
- **Added** `Assets/Scripts/Songs/`: `SongEntry` (JSON model), `SongNotation` (parse/convert to Canady's `SongData`/validate),
  `SongDatabase` (Resources/Songs loader: `Get`, `GetSection`, `All`, `ForDifficulty`), `MidiSongImporter` (own .mid reader, no
  DryWetMIDI), `SongAutoPlayer` (lights songs on PianoOverlay), `Editor/SongMenu` (Tools ▸ Songs ▸ Validate All / Print All /
  Import MIDI Files). jordan-branch read as reference only, **not merged**.
- **Songs** (`Assets/Resources/Songs/*.json`, typed by hand, all `verified: false`): hot_cross_buns (with fingering), twinkle_twinkle,
  ode_to_joy (fingering), chopsticks (bars 7–8 especially need checking), fur_elise A section bars 1–8 both hands,
  the_entertainer simplified opening bars 1–4 (needs checking). Not done: Clair de Lune and Für Elise 9–22 (import a PD MIDI),
  River Flows in You (copyrighted → `Assets/Resources/Songs/Local/` / `Assets/Music/Local/`, gitignored; nothing added).
- **Verified (Editor via UnityMCP):** all 6 songs pass Validate (0 errors, 0 warnings); Für Elise RH = Jordan's table note-for-note
  (35/35); Ode to Joy = AIDebugPanel.OdeToJoy() (38/38); MIDI round trip of Hot Cross Buns (written as a 2-track .mid, re-imported) =
  22/22 notes, tempo + 4/4 correct; `GetSection(..., RH)` and bar ranges work; `CoachContext.song = SongDatabase.Get(id)` works.
- **Found:** SongFormatter (AI) drops rests and pickups in what the coach reads; two `SongNote` classes (Lesson vs AI) — both added to
  "Requested" in INTERFACES.
- **Next:** someone checks the songs against sheet music and flips `verified`; import Clair de Lune / Für Elise from public-domain MIDI;
  lesson engine/evaluator loads songs from the DB.

## 2026-11-14 night — Codex (for Ari): startup mic permission and Main device checklist (handoff commit 9)
- `AppFlow` requests Android microphone permission before the intro and waits for the system response. An existing
  grant skips the dialog. Denial or dismissal skips recording and continues to calibration without a second request.
- Updated `CLAUDE.md` and `INTERFACES.md` for Main's wait-mode flow. `AURA_MAIN_TEST.md` covers permission cases,
  passthrough, calibration, real MIDI, the full excerpt, and the coach reply.
- **Verified:** Editor compile and Android development build succeeded with zero errors (Android build reported 355 warnings).
  `Builds/AuraMain-commit9.apk` declares `RECORD_AUDIO`. Installed on the connected Aura puck with `adb install -r`.
  The installed app resolves its launch activity, and the puck reports no mic grant or prior user-choice flags.
- **Pending hands-on test:** open ImmerseTheBay2026 on the puck with the glasses on and follow the checklist. The APK
  was built without cloud credentials. Check recording starts, then expect fallback feedback rather than a cloud transcript or reply.

## 2026-11-14 night — Codex (for Ari): sheet panel follows calibration (handoff commit 8)
- `AppFlow` finds the evaluator's `SongNoteDisplay` and positions it from the midpoint of the keyboard's lowest/highest
  drawn key centers on every `Calibrated` event, including later recalibrations. Default `sheetOffset`: 0 m sideways,
  0.45 m up, 0.20 m toward the fallboard. The panel follows keyboard yaw, stays upright and keeps its existing size.
- Placement happens before first activation, so the generated canvas inherits it. Recalibration does not restart the
  lesson, speak the lesson-start line again, or create another canvas. No scene asset changes or other-owner script edits.
- **Verified via Unity MCP:** no compile errors. Clean Main Play-mode run reached Lesson through normal startup and
  scripted calibration; measured offset exactly (0, 0.45, 0.20). Recalibration translated the piano, rotated it 25 degrees,
  and narrowed its span to 1.14 m: panel position error 0 m, yaw 25 degrees, scale unchanged, note index stayed 1, one canvas.
  Screenshot visually checked at `Temp/AppFlowVerification/appflow-sheet-calibration.png` (local, not committed).
- The clean run had no application exceptions; the same six missing-XR/Direct-Preview/secret environment errors remain.
  An earlier test-harness attempt cleared VoiceInput during its coroutine and was discarded; the clean rerun left startup intact.
- **Still not done (commit 9, outside this handoff's stopping point):** startup microphone permission, broader Main docs cleanup,
  and a full Aura/piano test. Cloud coaching/audio also needs local credentials. No push performed.

## 2026-11-14 night — Codex (for Ari): finished wait-mode runs reach the coach (handoff commit 7)
- `AppFlow` subscribes/unsubscribes `SongFinished`, converts the evaluator's actual 35-note right-hand excerpt into
  `SongData`, and sends `RunFinished` / `freeplay` context with `RunStats`, bars 1–8 and recent attempt history.
- Main allows only the `None` coach action because it has no demo/fingering player. The existing orb bridge handles replies.
- **Verified via Unity MCP:** script compilation completed with no compile errors; Main reached Lesson after scripted
  four-point calibration. A timed 35-note run sent 100% accuracy and displayed the fallback on the orb. A second run after
  disabling/re-enabling AppFlow sent one wrong press (77 instead of 76), 97.14% accuracy, and exactly two attempts in history.
- **Limitations:** no `secrets.json` exists on this machine, so cloud coaching/audio was not tested; native TTS is silent in
  the Editor. Play mode reports unavailable XR session/camera/eye/hand tracking, Direct Preview adb and missing-secret
  diagnostics. No AppFlow exceptions were reported. No device test, scene save, or changes to other owners' scripts.
- **Next:** place the sheet panel relative to calibration (commit 8); microphone startup permission remains commit 9.

## 2026-11-14 night — Claude Code (for Jordan): lesson lights the keys on the overlay
- `AppFlow` forwards evaluator events to `PianoOverlay.SetKey`: the note to play is `Next`, a right press flashes `Correct`,
  a wrong press flashes `Wrong` on the key that was pressed.
- **Works (Editor Play mode, scripted):** E5 `Next` → F5 `Wrong` → E5 `Correct` → D#5 `Next`. Untested on the Aura.
- **Known:** the calibrator's 5 s "all C keys" preview clears the keys it lit, so a C that is `Next` in that window goes dark once.
- **Next (not done):** send finished runs to the coach, place the sheet panel from the calibrated overlay, mic permission at startup.

## 2026-11-14 night — Claude Code (for Jordan): `AppFlow` sequences Main (intro → listen → calibrate → lesson)
- New `Assets/Scripts/App/AppFlow.cs` + an `App Flow` object in `Main`. `PianoCalibrator.calibrateOnStart` is off in `Main` only.
- Order: coach speaks a fixed intro and asks "are you ready", mic opens 4 s, calibration starts, and on `Calibrated` the evaluator
  object is switched on. The overlay is hidden until calibration starts.
- **Works (Editor Play mode):** one full run with real Groq TTS + Whisper (silence came back as "Thank you.", which is fine: any
  answer moves on); a second run with voice off and scripted calibration points reached `Lesson` with the evaluator active. 0 errors.
- **Not yet:** mic permission on Android is only asked at the first listen, so the first answer is missed there (fixed in a later
  commit). Untested on the Aura.

## 2026-11-14 night — Claude Code (for Jordan): Main gets the MIDI reader, evaluator and sheet panel
- Moved `MidiPianoReader` and `Performance Evaluator` (`PerformanceEvaluator` + `SongNoteDisplay` with the 21 sheet lines) from
  `JordanScene` into `Main` (JordanScene itself is unchanged). The evaluator object **starts inactive**; nothing turns it on yet.
- Left behind: JordanScene's rig, Plane, light, EventSystem and the MIDI Log Canvas.
- **Works (Editor Play mode):** activating the evaluator by hand builds the sheet panel and it advances on key presses, 0 errors.
  **Not yet:** the panel still sits at its JordanScene position (0.95, 1.5, 1.8), not relative to the piano.

## 2026-11-14 night — Claude Code (for Jordan): `Main.unity` = Dev_Ari + coach orb + AI coach
- `Assets/Scenes/Main.unity` is a copy of `Dev_Ari` (rig, AR Session, PianoOverlay, PianoCalibrator untouched) plus the `CoachOrb`
  prefab and an `AI Coach` object (AudioSource, `VoiceOutput`, `AICoach`, `VoiceInput`; no `AIDebugPanel`, its keys clash).
- Build scenes: `Main` at index 0; `Dev_Ari`, `SampleScene`, `JordanScene` are in the list but disabled.
- **Works (Editor Play mode, no headset):** orb idles, the bridge finds the coach / voice in / voice out, calibration starts as in
  `Dev_Ari`, 0 errors. **Not yet:** MIDI, evaluator, any sequencing. Untested on the Aura.

## 2026-11-14 night — Claude Code (for Jordan): coach menu compiles again (Nehemiah's folder, with Jordan's OK)
- `Assets/Scripts/Coach/Editor/CoachOrbMenu.cs`: **Tools ▸ Coach ▸ Add AI Coach + Orb Bridge** no longer adds `HesitationTracker`
  or sets `AIDebugPanel.tracker`; both were deleted on Canady's branch. Nothing else in the menu changed.
- **Works:** 0 compile errors in the Console, so Play mode is available again.

## 2026-11-14 night — Claude Code (for Jordan): Lesson scripts compile again after the AI merge
- Main had 6 compile errors in `Assets/Scripts/Lesson/` after merging Canady: `SongNote` defined twice, `PerformanceMetrics`
  deleted, `WrongNote` changed to MIDI numbers.
- Lesson types renamed `Song` → `LessonSong`, `SongNote` → `LessonNote`. `PerformanceEvaluator.SongFinished` now hands out the
  coach's `RunStats` (accuracy, timing, `tempoRating`, wrong notes, rushed / dragged bars). `avgTimingErrorMs` and `songName` are gone.
- **Works:** the runtime assembly compiles. **Still broken:** `Assets/Scripts/Coach/Editor/CoachOrbMenu.cs` references the deleted
  `HesitationTracker` and `AIDebugPanel.tracker` (2 errors, editor assembly), so Play mode is still blocked.

## 2026-11-14 evening — Claude Code (for Jordan): coach playbook linked from the docs
- `docs/COACH_PLAYBOOK.md` (already on main) is now referenced from `CLAUDE.md` (Architecture) and `INTERFACES.md` (LessonEngine).
  It defines the lesson progression: learn each new measure alone at 50% → 75% → 100% tempo, then a full play-through from
  measure 1 at song tempo, then repair any Problem measures (with a lead-in when the measures before were shaky).
- **Not implemented yet.** `PerformanceEvaluator` (`Assets/Scripts/Lesson/`) still plays the whole Für Elise excerpt at one tempo.
  Next: per-measure ratings, a tempo rating per run, section looping and tempo steps in the evaluator.

## 2026-11-14 ~19:20 — Claude Code (for Nehemiah): orb glows brighter when speaking; words appear in time with the voice
- **Glow:** new `Glow` child in the CoachOrb prefab: additive halo quad (`Assets/Scripts/Coach/Materials/CoachOrbGlow.mat`,
  URP Unlit transparent/additive, runtime radial texture) behind the sphere, strength per state (idle 0.12 → speaking 0.45–1.0
  with the voice). Speaking core is pushed toward white with the voice. Also: the sphere floats (6 mm bob).
- **Words in time with speech (CoachOrbBridge):** Groq joins the line into one clip, so words follow `source.time / clip.length`
  (lead/tail silence trimmed, extra weight for pauses at punctuation). Native TTS: estimated 14 chars/s. Orb stays Thinking
  with "..." while Groq audio downloads. Uses read-only reflection on VoiceOutput's private `groq` to know if Groq gave up.
- **Verified (Editor):** reveal mapping over a 6 s line: 0.6 s "Nice and steady." … 3.3 s "...thumb on C, index on D," … 6 s full;
  simulated clip → "words follow the audio"; glow 0.63–0.67 speaking vs 0.12 idle; 0 errors.
- **⚠ Groq TTS quota:** hit the free tier's **3,600 tokens/day** limit for `canopylabs/orpheus-v1-english` (HTTP 429, org-wide,
  shared by everyone using that key) — roughly a few dozen spoken replies a day. Coach falls back to native TTS (silent in the
  Editor, works on Android). For the demo: upgrade the Groq org (Dev Tier) or rely on native TTS on the Aura.
- Unity keeps re-serializing three XRI sample prefabs in `Assets/Samples/` (new `m_Priority` fields); reverted, not committed.

## 2026-11-14 ~19:00 — Claude Code (for Nehemiah): CoachOrb drag-and-drop prefab, BasicScene_Coach
- **Prefab:** `Assets/Scripts/Coach/Prefabs/CoachOrb.prefab` = CoachOrb + Text + CoachOrbBridge, no scene references.
  Bridge now looks for the orb on its own object first and searches the scene at most once a second when something is missing.
  `debugKeys` toggle on CoachOrb (T/Y/H/V) and CoachOrbBridge (G). Menu: **Tools ▸ Coach ▸ Rebuild CoachOrb Prefab**;
  "Add Coach Orb" / "Add AI Coach + Orb Bridge" now place the prefab (the AI coach object no longer gets its own bridge).
- **Scenes:** `Dev_Nehemiah` and new `BasicScene_Coach` (copy of Jordan's BasicScene + Jordan's passthrough camera: solid
  (0,0,0,0) background, no ARSession) use the prefab + an AI Coach object.
- **Verified (Editor Play mode via UnityMCP):** prefab dropped into BasicScene_Coach auto-found orb/coach/voice in/out →
  Thinking → Claude reply → Groq TTS → Speaking. Prefab alone in an empty scene: placed itself 0.68 m / eye level, ShowText
  shown and faded, 0 errors.
- **Not done:** BasicScene_Coach still has the sample rig's locomotion/gravity on and the grid Plane; no ARSession (Ari saw
  OPAQUE/90% dimming without it). Jordan's newest commit (`165021c` Performance Evaluation) isn't merged: its
  `Assets/Scripts/Lesson/SongData.cs` defines `SongNote`, which clashes with Canady's `Assets/Scripts/AI/SongData.cs`.

## 2026-11-14 ~18:15 — Claude Code (for Nehemiah): CoachOrb hooked up to the AI coach
- Merged `origin/Canady` (`9c4984f`: CoachContext/CoachFacts/CoachAction, SongData, CoachPrompt.txt) into `nehemiah` first. No conflicts, no keys.
- **Added** `Assets/Scripts/Coach/CoachOrbBridge.cs` (AI scripts untouched): Listening > Thinking > Speaking > Idle from the AI flags,
  `"..."` while thinking, `OnResponse` → `ShowText(say)` held while speaking + 2 s, voice level from `VoiceOutput.source` RMS
  (synthetic pulse for native TTS). Editor key `G` = fake "run finished". Menu **Tools ▸ Coach ▸ Add AI Coach + Orb Bridge**.
- **Dev_Nehemiah:** new `AI Coach` object (AudioSource, VoiceOutput, AICoach, VoiceInput, HesitationTracker, AIDebugPanel, CoachOrbBridge).
- **Verified in Editor Play mode (via UnityMCP):** with no `secrets.json` on this machine the coach falls back in ~66 ms →
  orb shows the fallback line, speech never starts → text kept 2.5 s. Simulated slow reply: Idle → Thinking + `"..."` → reply text →
  Speaking. 0 script errors.
- **Real keys (secrets.json added locally, gitignored):** G-style "Improving" run → Thinking + `"..."` → Claude (Haiku) reply in ~1.9 s
  (`show_fingering bar 3`) → reply shown → Speaking → Idle; text kept 9.4 s (reading time). **Groq TTS fails: HTTP 400
  `model_terms_required`**: the Groq org admin must accept the `canopylabs/orpheus-v1-english` terms in the Groq console. Until then
  the coach falls back to native TTS (silent in the Editor, works on Android).
  **Update:** the key owner accepted the Groq terms; retested: `TTS Groq ... OK in 829 ms`, 15.6 s clip played through
  `VoiceOutput.source`, orb Speaking → text faded ~2 s after speech ended.
- **Text limit raised:** `CoachOrb.maxCharacters` 120 → 300 and `textWidth` 0.30 → 0.36 m (~40 chars/line, ~8 lines max):
  coach replies were 160–245 chars; a 245-char reply now shows in full.
- **Not verified:** RMS voice level from real audio (the Editor was in the background, so audio/frames were paused; the synthetic
  pulse path did run). Needs Unity focused + a Groq key, or the Aura. Native TTS pulse and readability: Aura only.
- Requests for the AI owner added under "Requested" in INTERFACES (speaking/thinking events, TTS-started signal, native TTS level).

## 2026-11-14 ~18:00 — Claude Code (for Ari): docs caught up with the merged branch
- CLAUDE.md: owners (MIDI = Jordan, AICoach = Canady, LessonEngine still TBD), current dev scenes, AI key setup (secrets.json
  in StreamingAssets ships inside the APK), recording/alpha and hand-material-slot rules, new "Device tooling" section
  (glasses capture, wireless adb, logs, probed Aura runtime features).
- INTERFACES.md: PerformanceMetrics / CoachResponse shapes, VoiceInput / VoiceOutput / AIConfig, LessonEngine contract, and the
  open requests (LessonEngine owner, MIDI → overlay on hold, IsConnected, CoachOrb wiring, MIDI folder move).
- Added the MIT `LICENSE` at the repo root (required for the Devpost submission; CLAUDE.md already referred to it).
- Merged branch compiles in Unity (0 script errors). Not built for the puck yet.

## 2026-11-14 ~17:45 — Claude Code (for Ari): branch ARI-MAIN-MERGE-531 = ARI + main
- New branch from ARI (ARI itself unchanged); main merged in (MIDI reader + Android MidiBridge, AI coach, Jordan's scenes).
- Conflicts: `Packages/manifest.json` + lock kept ARI's (MCP pinned to v10.0.0, `com.google.xr.extensions` kept, and
  `com.unity.ai.assistant` NOT re-added: it clashes with MCP for Unity, see CLAUDE.md; main's code doesn't use it).
  Build scenes: Dev_Ari first, then main's JordanScene. `Assets/Scripts.meta`: main's folder GUID.
- INTERFACES.md now lists the real MidiPianoReader and AICoach APIs from main.
- Not yet opened/compiled in Unity on this branch; MIDI is not wired to PianoOverlay yet.

## 2026-11-14 ~17:45 — Claude Code (for Nehemiah): CoachOrb (floating coach orb + text)
- **Added** `Assets/Scripts/Coach/CoachOrb.cs` (implements the INTERFACES `CoachOrb` contract + `ShowText`/`HideText`) and
  `Assets/Scripts/Coach/Editor/CoachOrbMenu.cs` (Tools ▸ Coach ▸ Add Coach Orb / Add Aura XR Rig to Scene), material
  `Assets/Scripts/Coach/Materials/CoachOrbUnlit.mat` (URP Unlit). Put in `Scripts/Coach/`, not `Scripts/UI/` (Ari's folder).
- **Dev_Nehemiah scene:** replaced the plain camera with the project's hands XR rig + AR Session + ARCameraManager (passthrough, locomotion off,
  same as Dev_Ari) and added the CoachOrb.
- **Verified in Editor Play mode (via UnityMCP):** compiles with 0 errors/warnings; orb places itself at eye level, 0.68 m away, 36° right;
  text wraps beside the orb, faces the player, long text truncated at 116 chars; Speaking color/pulse works.
- **Untested on the Aura:** readability of 0.18 text at ~0.7 m, whether 36° right is comfortable/in view, colors on the additive display,
  piano-anchored placement (Dev_Nehemiah has no PianoOverlay; it anchors automatically in a scene that has one).
- **Next:** AI coach calls `SetState(Thinking)` while waiting, `SetState(Speaking)` + `SetVoiceLevel` during TTS, and `ShowText(response.say)`.

## 2026-11-14 ~17:30 — Claude Code (for Ari): hand occluder back to the first (depth-only) version
- The soft black-mask occluder (~16:40 entry below) was wrong: it drew the hand instead of only hiding the overlay. Reverted to the
  first version: `PianoTeacher/HandOccluder` is depth-only again (ColorMask 0, ZWrite On, queue 1990), mesh grown +4 mm
  (`_Inflate` 0.004). Shrinking the mesh let the overlay show through at the finger edges.
- Fix kept from the investigation: the template hand meshes have TWO material slots (DepthOnly + the grey `Unity_Hand_Medium`
  glove). Replacing only slot 0 left the glove drawing an outline around the hands. `PianoCalibrator` now replaces every slot.
- Hand shape is the generic template hand posed by XR Hands: joint positions/finger lengths follow the tracked hand, width
  and thickness are generic. The runtime hand meshes (XR_ANDROID_hand_mesh / XR_FB_hand_tracking_mesh) failed every query in
  an on-device test and are not used; those features are off again.
- Tooling: `adb exec-out screencap` doesn't work (two displays); `adb shell screencap -d 4626964206039778834 -p <file>` captures
  the glasses' mixed-reality view (camera + app). Wireless adb works over a private hotspot.

## 2026-11-14 ~17:15 — Claude Code (for Nehemiah): merged `main` into `nehemiah`
- Merged `origin/main` (up to `e81d0de`: TTS, AI coach incl. Claude/Groq providers, `MidiPianoReader`, JordanScene) into `nehemiah` (`79f877d`).
- **Conflicts:** kept `nehemiah`'s `manifest.json` / `packages-lock.json` (MCP pinned `#v10.0.0`, no AI Assistant) over main's
  MCP-on-`#main` entry; took main's `Assets/Scripts.meta` GUID (ARI's differs, so ARI→main will conflict on it: keep main's);
  build settings = `Dev_Ari` (index 0, startup scene) + `JordanScene`, both enabled.
- Added `Assets/Scenes/Dev_Nehemiah.unity` (empty: Main Camera + Directional Light).
- **Works:** Unity recompiled with all merged scripts; Console 0 errors / 0 warnings (checked via UnityMCP). Not tested in Play mode or on device.
- **Note for `main`:** main still lists `com.unity.ai.assistant` and MCP on `#main`; when ARI/nehemiah lands on main, take the pinned/removed version.

## 2026-11-14 — Claude Code (for Nehemiah): merged ARI into `nehemiah`, Unity MCP verified on Nehemiah's machine
- **Merged `origin/ARI` into `nehemiah`** (commit `91b1cb2`). Discarded local uncommitted edits first: MCP on `#main` (ARI pins `#v10.0.0`)
  and an `OpenXRPackageSettings.asset` change that had turned Android XR Support / Hand Interaction / Foveation **off** (editor churn).
  125 modify/delete conflicts on `Packages/com.unity.ai.assistant/*.meta` resolved by deleting (package removed on ARI). Removed the
  leftover empty `Packages/com.unity.ai.assistant/` folders so Unity doesn't see an embedded package. `main` (Canady's TTS/AI coach) is not merged yet.
- **Prereqs:** Claude Code 2.1.325, uv 0.13.8, Python 3.13.0.
- **Works:** `claude mcp list` → `UnityMCP: http://127.0.0.1:8080/mcp (HTTP) ✔ Connected` (server `mcp-for-unity-server` 3.4.11, 47 tools).
  Read-only check: active scene is an unsaved untitled scene with 2 roots (Main Camera, Directional Light). Console: 0 errors, 0 warnings
  after the reimport. Scene not modified.
- **Gotcha:** UnityMCP tools only show up in a Claude Code session started *after* the server is running; restart Claude Code if they're missing.
- **Next:** open `Dev_Ari` / `MIDI OVERLAY` to confirm they load cleanly here; merge `main` once ARI lands there.

## 2026-11-14 ~16:30 — Claude Code (for Ari): hands occlude the overlay
- New shader `Assets/Scripts/UI/Shaders/HandOccluder.shader` (`PianoTeacher/HandOccluder`): depth-only (ColorMask 0, ZWrite On,
  queue 1990), stereo-instancing safe, inflates the mesh 4 mm along normals to cover tracking error. Material at
  `Assets/Scripts/UI/Resources/HandOccluder.mat` (loaded at runtime, so no scene change).
- `PianoCalibrator`: once calibrated (`handsOccludeOverlay`, default on), the tracked hand meshes get the occluder material and
  follow tracking instead of being hidden, so overlay keys under a hand aren't drawn and the real hand shows through. While
  calibrating the meshes stay hidden as before, so pinch markers remain visible.
- Built and installed on the puck; not yet checked on the glasses. The template hand mesh is a generic size, so occlusion
  edges may be a few mm off for small or large hands (tune `_Inflate` on the material).
- ~16:40 update after a device test ("occluded zone too big, edge too clean"): the occluder is now a soft black mask drawn
  after the keys (queue 2450, alpha blend, ColorMask RGB, ZWrite Off). Alpha = smoothstep(0, `_EdgeWidth`, |N·V|), so it
  fades from 0 at the hand outline to full just inside it; `_Inflate` now -1.5 mm (shrinks the generic mesh). Cost: one extra
  draw per tracked hand, no render targets or full-screen passes. Depth-map occlusion (XR_ANDROID_depth_texture) was
  considered and rejected: a per-pixel depth sample in every overlay shader plus the runtime's depth computation, with
  coarser edges than the hand mesh at finger scale.

## 2026-11-14 ~16:10 — Claude Code (for Ari): brighter overlay, standard black-key layout, Aura tracking check
- Faint overlay cause: idle keys were 0.10 grey in Linear color space (≈1% light on an additive display), drawn only on the back
  65% of each key. `PianoOverlay` now draws idle keys as bright outlines (`outlineKeys`, `outlineColor`, `outlineWidth`); lit keys
  get a filled top plus outline in the state color. White-key side lines stop where a black key covers them.
- Black keys now follow the standard layout (C#/D# split C–E into 5 parts, F#/G#/A# split F–B into 7) instead of sitting centered
  on the white-key boundary; `blackKeyRaise` = 12 mm. White-key sizes were already standard (octave 164.5 mm).
- Aura runtime check (diagnostic build, not committed): the Android XR runtime lists XR_ANDROID_trackables_marker, _qr_code,
  _image, depth texture, raycast and scene meshing. Camera CPU images are NOT available to Unity scripts (as Unity's Android XR
  docs state). Next: marker tracking (ArUco on a phone screen) to place the overlay without pinching.
- Built and installed on the puck; outline look not yet checked on the glasses.

## 2026-11-14 ~15:20 — Claude Code (for Ari): four-point order is now far end → near end
- Real cause of "back points aren't behind the front points": the prompts said "FRONT edge" / "BACK end", which Ari (sitting at
  the piano) read the opposite way. Device log showed every second pinch 14–18 cm toward the player = pinched in reverse.
- New order per key: **FAR end (where the key goes under the panel) first, then NEAR end (closest to you)**; far-left key, then
  far-right key. The reverse order is accepted too: each key's two pinches are sorted into near/far by direction toward the panel.
  Prompts and error messages say "far end" / "near end" now.
- Every four-point pinch is logged with its position relative to the head (right/up/forward cm). Not tested in Play mode; built and
  installed on the puck for an on-device test.

## 2026-11-14 ~15:10 — Claude Code (for Ari): fix "back points aren't behind the front points"
- Cause: steps 2/4 only checked the 3D distance (6–25 cm, any direction), but the final fit needs the back points to be
  behind the front ones on the level, toward the panel. A pinch fired while lifting the hand passed the step check, failed
  the fit, and all four points were thrown away. The device log also showed the other, resting hand firing stray pinches.
- `PianoCalibrator`: back pinches must now be ≥ `minBackDepth` (8 cm) back toward the panel (gaze direction for the left key,
  the left→right line for the right key) and within `maxBackHeightDelta` (6 cm) of the front pinch height, else only that step repeats.
  A key's back pinch is only accepted from the hand that pinched its front edge (the other hand's pinches are ignored).
  `SubmitPoint(Vector3, int hand)` overload added (0 = left, 1 = right, -1 = unknown); the old `SubmitPoint(Vector3)` still works.
  When the fit is rejected, the four points and both depth vectors are logged.
- **Works (Editor play mode, scripted):** clean four points calibrate; other-hand pinch ignored; lifted (3 cm back) and sideways
  (2 cm back) back pinches rejected at that step; then good pinches calibrate (key length 13.7 cm). **Untested on the Aura.**

## 2026-11-14 ~15:05 — Claude Code (for Ari): four-point calibration, Recalibrate dot on the left hand
- Device log: a good two-hand calibration (116 cm, scale 0.968) was overwritten 9 s later by a mid-air pinch (87 cm, right point
  ~9 cm above / 25 cm in front of the keys) after Recalibrate. Simultaneous pinches were also awkward at 1.2 m apart.
- **Four-point calibration (default):** front edge + back end of the far-left key, then of the far-right key, one hand at a time.
  Each step is validated (key depth 6–25 cm, right side 85–115% of ~1.20 m and to the right, depths/back span consistent);
  a stray pinch repeats only that step. Fit keeps side-to-side tilt, measures real key length; front-to-back stays level
  (14 cm baseline is too noisy for pitch). Span tolerance tightened 60–160% → 85–115%.
- **Recalibrate dot** on the back of the LEFT hand (visible when the back of the hand faces you); right index touch-and-hold 0.6 s.
  The floating Recalibrate tile is disabled (it was hidden mid-calibration and sat under the A0 pinch).
- Verified in Editor Play mode (scripted): stray 40 cm back point, 87 cm right point and mismatched depth each rejected with only that
  step repeated; good points → scale 1.001, roll 2.00°, key length 14.0 cm, A0/C8 error 0.0 mm. Hand dot needs on-device testing.
- **Android XR navigation:** still unexplained. Built manifest has `XR_ACTIVITY_START_MODE_FULL_SPACE_UNMANAGED` and Quest leftovers
  (`com.oculus.intent.category.VR`, `com.oculus.permission.HAND_TRACKING`); unproven. Needs a log captured at the moment it fails.

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
