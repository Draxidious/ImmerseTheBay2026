# INTERFACES.md — public hooks between modules

The contract between modules. If it isn't listed here, don't depend on it.
**Update this file in the same commit as any change to a public API.**

Status legend: ✅ implemented · 🚧 in progress · 📝 agreed, not built yet

**Main scene = `Assets/Scenes/Main_2.unity`** (the only enabled build scene). `Main.unity` is the older freeplay demo, disabled.

---

## Shared types

```csharp
public enum KeyState { Off, Upcoming, Next, Correct, Wrong }   // ✅ defined in PianoOverlay.cs
public enum OrbState { Idle, Listening, Thinking, Speaking }   // ✅ defined in Assets/Scripts/Coach/CoachOrb.cs
```

MIDI note numbers throughout: middle C = 60, P71 range = 21 (A0) to 108 (C8).

---

## App flow — `AppFlow` (`Assets/Scripts/App/AppFlow.cs`) ✅ — Jordan
The only script that knows the order of the app in the main scene `Main_2.unity` (also in the old `Main.unity`). It calls the public members below on the other modules and
holds no lesson or AI logic. Steps (`AppFlow.Step`, `CurrentStep`): `MicrophonePermission` → `Intro` → `Listening` → `Calibrating` → `ChooseMode` → `Lesson` or `FreePlay`.

0. `MicrophonePermission`: hides overlay/evaluator, requests Android microphone permission when needed and waits for
   grant, denial (including don't-ask-again), or dismissal before the intro. No prompt when already granted, in the Editor,
   or without a VoiceInput. A denial/dismissal skips recording; it does not block calibration or prompt again in Listening.
1. `Intro`: speaks `introLine` (`VoiceOutput.Speak`, text via `CoachOrb.ShowText`).
2. `Listening`: with permission, `VoiceInput.StartListening()` for `listenSeconds` (4 s), then `StopListening()`.
   Permission is rechecked before listening. Any answer, silence, or unavailable recording moves on.
3. `Calibrating`: shows the overlay, calls `PianoCalibrator.Recalibrate()` (the scene has `calibrateOnStart` off), speaks `calibrateLine`.
4. `ChooseMode`: on the first `PianoCalibrator.Calibrated`, sets `AppModeSwitch.AfterCalibration`, shows `ModeChoicePanel.ShowChoice()`
   and speaks `chooseModeLine`. In Main_2 the player touches **Menu**, swipes toward a mode, then lifts to choose.
   `AIConfig.defaultMode` = `Lesson` / `FreePlay` skips the choice.
5. `Lesson` (`EnterLesson`): ends free play, `PianoOverlay.ClearAll()`, activates the `PerformanceEvaluator` object, `Restart()`s it
   (always from the first note) and speaks `lessonLine`. `FreePlay` (`EnterFreePlay`): deactivates the evaluator object (stops judging
   and hides its sheet panel), `ClearAll()`, `FreePlayController.Begin()`, speaks `freePlayLine`. In Main_2 both modes use the
   radial menu for switching. Older scenes can still show "Switch mode". AppFlow handles `AppModeSwitch.Requested` after calibration, except while recalibrating.

During `Lesson` it drives `PianoOverlay.SetKey` from the evaluator: `Restarted` / `NoteJudged` → `Next` on the note to play,
`NoteJudged` → `Correct` flash, `WrongNotePlayed` → `Wrong` flash on the pressed key.

**With a `CurriculumEngine` in the scene (`Main_2`)** AppFlow runs the guided lesson instead: it disables the
`PerformanceEvaluator`, activates the sheet panel and calls `engine.StartLesson()` after calibration (the coach's own
lesson-start line replaces `lessonLine`), and sends nothing to the coach itself. Keys come from the engine's events:
`OnRunStarted` → the first note `Upcoming`, `OnNextNote` → `Next` until the note is over plus the following note
`Upcoming`, `OnNotePlayed` → `Correct` / `Wrong` flash, demo notes → `Next`. All with the note's hand, so left-hand keys
are amber. Editor keys in the lesson: Enter = skip waits, F5 clean run, F6 one wrong note, F7 two wrong notes in the
last measure, F8 30% too slow.

`SongFinished` sends `RunStats` to `AICoach.Coach` in a `CoachContext` (`RunFinished`, `freeplay`).
The context's song comes from the song database: `LessonSongSource.ForCoach(evaluator.songId, evaluator.songSection)`,
i.e. the right-hand section the evaluator judged (including the pickup) (fallback: built from the evaluator's own notes),
plus target tempo, attempt count and the last five accuracy scores. `coach` is found automatically when unassigned.
Only `CoachActionType.None` is allowed: Main has no demo/fingering action player. `CoachOrbBridge` presents the
reply and voice state as usual; missing keys use the coach's existing fallback. The note loop never waits for this request.

On every `Calibrated` event, AppFlow places `sheetDisplay` (`SongNoteDisplay` on the evaluator object) at the midpoint
between the overlay's lowest/highest drawn key centers plus `sheetOffset` (default 0 m sideways, 0.45 m up, 0.20 m
toward the fallboard). The offset is in meters and follows keyboard yaw, not calibration scale or roll. The panel stays
upright and faces the player side of the keyboard. It is positioned before the first activation and repositioned after
later calibrations without restarting the evaluator. `sheetDisplay` is found on `evaluator` when unassigned.
In `Awake`, AppFlow calls `LessonSongSource.ApplySheets(sheetDisplay, evaluator.songId, evaluator.songSection)`, so the panel
shows the generated sheet strips of the evaluator's song (fallback: the images set in the scene). Code only, no scene change.

The lines are fixed text on the component (no model call). Editor debug: `Enter` = skip the current speech / listening wait.

---

## UI — owner: Ari

`Main_2` has a `FingeringGuide` on App Flow. It consumes the curriculum's existing run, due-note, and demo events.
It uses authored `ExpectedNote.hand` and `finger` to pair lilac key badges (e.g. L1) with a glow on the target fingertip.
A correct MIDI note with the authored fingertip close to the key produces a brighter pulse. **Fingering On/Off** controls both cues.
The setting defaults to on and is saved on the device.
`SongNoteDisplay` routes coach fingering help to an authored overlay replay when the guide is on and can replay the requested
bar. Otherwise it displays the existing sheet text. In Editor Play mode, **F10** toggles simulated fingertip positions.

### PianoOverlay (`Assets/Scripts/UI/PianoOverlay.cs`) ✅
88-key overlay generated in code. Local frame: +X along the keyboard (left to right), +Z toward the fallboard, +Y up.

| Member | Purpose |
|---|---|
| `void SetKey(int midiNote, KeyState state)` | Set one key's state. `Correct`/`Wrong` auto-revert to `Off` after `flashSeconds`. |
| `void SetKey(int midiNote, KeyState state, PianoHand hand)` | Same, with the hand that plays it: `Upcoming` / `Next` are amber for `PianoHand.Left` (`leftUpcomingColor`, `leftNextColor`), cyan for the right. `Correct` / `Wrong` look the same for both. Added by Jordan's agent, 2026-11-14. |
| `void ClearAll()` | All keys `Off`. |
| `void SetRange(int low, int high)` | Rebuild for a different keyboard range. Default 21 to 108. |
| `Vector3 GetKeyWorldCenter(int midiNote)` | World position of a key's drawn area (for sheet-to-key lines, effects). |
| `void CalibrateFromTwoKeys(Vector3 lowFront, Vector3 highFront)` | Snap onto the real piano from two pinched points: the front-edge center of the lowest and highest key. |
| `static bool IsBlack(int midiNote)` | Helper. |
| `string CalibrateFromFourPoints(Vector3 frontLow, Vector3 backLow, Vector3 frontHigh, Vector3 backHigh)` | "front" = the key's near end (closest to the player), "back" = its far end (where it goes under the panel), for the lowest and highest key. Sets position, scale, side-to-side tilt (≤ `maxTiltDegrees`) and the real visible key length (keys rebuilt). Returns null on success or a reason. |
| `void SetIdleKeysVisible(bool visible)` | Off keys are drawn as bright outlines (`outlineKeys`, `outlineColor`, `outlineWidth`) so the keyboard is visible on the additive display; turn off to hide them (e.g. during a demo shot). Lit keys get a filled top plus outline in the state color. |
| `void Build()` | Rebuild all key objects (called by `SetRange` and four-point calibration). |
| `void SaveCalibration()` | Persist the current pose/scale to PlayerPrefs. Called automatically after calibration and after a grab-handle nudge. |

Editor debug: `1` = Next on C4 with Upcoming E4/G4, `2` = Correct C4, `3` = Wrong C#4, `0` = clear.

`CalibrateFromTwoKeys` keeps the keyboard's tilt along its length (up to `maxTiltDegrees`, default 5°); front-to-back stays level.
Key geometry uses standard sizes (octave 164.5 mm); black keys follow the standard layout (C#/D# split C–E into 5 parts,
F#/G#/A# split F–B into 7) and stand `blackKeyRaise` (12 mm) above the white keys.
The calibrator's post-calibration preview only clears keys it lit itself, so lesson highlights set meanwhile are kept.

Keys have no colliders. In Main_2 the hand menu provides recalibration and fine-tuning. Dev scenes can still use a grab handle.

### PianoCalibrator (`Assets/Scripts/UI/PianoCalibrator.cs`) ✅
Hand-tracking calibration (XR Hands thumb-tip/index-tip distance). Default is four pinches (`fourPointCalibration`):
far-left key's FAR end (at the panel) then its NEAR end, then the same for the far-right key; either order per key is accepted.
Calls `PianoOverlay.CalibrateFromFourPoints`. Shows a marker at each pinch and a floating prompt; every pinch is logged
(`[PianoCalibrator] Pinch for step …`, position relative to the head). Disables the overlay grab while calibrating; saves the
overlay pose when the grab is released.

Hand occlusion: after calibration the tracked hand meshes (`handMeshRenderers`) get the depth-only material
`PianoTeacher/HandOccluder` (`Resources/HandOccluder`, every material slot replaced but the slot count kept, mesh grown 4 mm; the rig's XRI hand affordances tint slot 1), so overlay pixels behind
a hand are not drawn and the real hand shows through. Toggle with `handsOccludeOverlay`; while calibrating the meshes are hidden.

| Member | Purpose |
|---|---|
| `void Recalibrate()` | Forget the points and wait for two new pinches. Also wired to the `RecalibrateButton` interactable. |
| `void SubmitPoint(Vector3 worldPoint)` | Feed a calibration point manually (non-hand input, tests); goes through the same step validation as a pinch. |
| `void SubmitPoint(Vector3 worldPoint, int hand)` | Same, tagged with the hand (0 = left, 1 = right, -1 = unknown). In four-point mode a key's back pinch is only accepted from the hand that pinched its front edge. |
| (default flow) `fourPointCalibration` | Four sequential pinches (far end then near end of the lowest key, then of the highest). Each step is validated (key length 6–25 cm, ≥ `minBackDepth` 8 cm toward the panel, within `maxBackHeightDelta` 6 cm in height, span 85–115% of ~1.2 m, right of the first key); a stray pinch repeats only that step, and a key's second pinch only counts from the hand that made its first. Recalibrate any time: touch-and-hold the orange dot on the back of the LEFT hand with the right index finger (~0.6 s). |
| `bool handsOccludeOverlay` / `Material handOccluderMaterial` | Hand occlusion on/off; occluder material (loaded from `Resources/HandOccluder` if empty). |
| `void SubmitHandPinch(bool isLeft, Vector3 worldPoint)` | Two-hand flow (`twoHandCalibration`, used only when `fourPointCalibration` is off): left hand on the lowest key + right hand on the highest, pinched within `twoHandWindowSeconds`. A lone pinch never calibrates. |
| `float ExpectedSpanMeters()` | Real-size distance between the lowest and highest key's front centers (≈1.20 m for 88 keys). |
| `event Action Calibrated` | Raised after a successful calibration (pinches or markers). |
| `bool IsCalibrating` / `Phase CurrentPhase` | `Phase { Idle, WaitingForLow, WaitingForHigh }`. |
| `event Action CalibrationStarted` | Raised by every `Recalibrate()`, so another calibration source (`MarkerCalibration`) can start. |
| `string CalibrateFromMarkers(Vector3 backLowOuter, Vector3 backHighOuter, bool useKnownWidth = true)` | Calibrate from the BACK OUTER corners of A0 and C8 (where they meet the panel). Corners are moved half a white key inward; the front points are one `whiteKeyLength` toward the player; with `useKnownWidth` only the centre and direction come from the corners (span = real keyboard width). Ends calibration and raises `Calibrated`. Returns null on success, else a reason. |
| `string ApplyMarkerAdjustment(Vector3 backLowOuter, Vector3 backHighOuter, bool useKnownWidth = true)` | Same fit without ending calibration or raising events (offset sliders). |
| `void ShowInfo(string text, float seconds)` | Temporary message on the calibration prompt. |
| `bool markersAvailable` (runtime) | Set by `MarkerCalibration`: pinches are ignored and the prompt asks for the corner markers. Cleared if marker tracking can't run that round, which brings the pinch steps back. |
| `event Action OffsetsDotPressed` | A second, blue dot sits just below the orange one (toward the wrist) while something listens; touch-and-hold it the same way (~0.6 s). |
| `event Action MenuDotPressed` | Orange dot held (~0.6 s, 2 s cooldown). While anything listens (the `RadialMenu`), the dot raises this **instead of** calling `Recalibrate()`; with no listener it recalibrates as before. Added by Nehemiah's session (Ari informed via this doc). |
| `void CancelCalibration()` / `event Action CalibrationCancelled` | Stop a calibration in progress; the overlay keeps the pose of the last successful calibration (it only moves when a fit succeeds). Hides markers and preview keys, shows "Recalibration cancelled". No-op when idle. |

Editor debug: `C` = fake both pinches in front of the camera, `R` = recalibrate.

### MarkerCalibration (`Assets/Scripts/UI/MarkerCalibration.cs`) 🚧 — Dev_Ari
Two printed ArUco 4x4_50 markers on the P71's rear corners (ID 0 behind A0, ID 1 behind C8, 33 mm symbols; print sheet
`output/pdf/Yamaha-P71-Corner-Markers-Letter.pdf`) take over `PianoCalibrator`'s four pinch steps. On `CalibrationStarted` it
asks for `android.permission.SCENE_UNDERSTANDING_COARSE`, enables the `ARTrackedImageManager` on the XR Origin, locks the
left then the right marker (≥30 samples over ≥1.2 s within 10 mm; marker span 1.18–1.32 m), turns tracking off and calls
`CalibrateFromMarkers`. Offsets (inward, down, toward player, m) from each marker centre to the key's back outer corner:
left `(-0.005, 0.016, 0.107)`, right `(0.015, 0.016, 0.090)` (saved on the Aura 2026-11-15). In the main scene `Main_2.unity` and `Dev_Ari`. The blue left-hand dot opens a
world-space UGUI panel (Left depth, Right depth, Height, Sideways; ±60 mm, `<` `>` = 1 mm; Save / Defaults / Close), driven by
the rig's XRI interactors through an `XRUIInputModule`. Saved to `marker-offsets.json` in persistent data. The keyboard overlay is hidden during the scan and shown when both markers lock (`hideKeyboardWhileScanning`). Falls back to pinches
on permission denial, tracker not running after 5 s, or no marker in `giveUpSeconds` (45 s). Log tag `[MarkerCalibration]`.
Setup: **Tools → Piano → Add Marker Calibration To Open Scene** (+ **Enable Marker Tracking (OpenXR Android)**, which turns on
Google's `XRSessionFeature` + `XRMarkerTrackingFeature`). Editor keys: `M` = simulate a marker lock, `O` = offsets panel.

### SheetPanel (`Assets/Scripts/UI/SheetPanel.cs`) 📝
| Member | Purpose |
|---|---|
| `void LoadPiece(string pieceId)` | Load the page images for a piece from `Assets/Music/<pieceId>/`. |
| `void HighlightMeasure(int measureIndex)` | Highlight the current measure; turns the page if needed. |
| `Vector3 GetNoteWorldPosition(int measureIndex, int noteIndex)` | (stretch) For drawing a line from a note to its key. |

### CoachOrb (`Assets/Scripts/Coach/CoachOrb.cs`) ✅ — built by Nehemiah (kept out of `Assets/Scripts/UI/`, Ari's folder)

**Drag and drop:** `Assets/Scripts/Coach/Prefabs/CoachOrb.prefab` (orb + text + `CoachOrbBridge`). Drop it anywhere in a scene,
no wiring needed: it places itself at runtime, anchors to a `PianoOverlay` if there is one, and follows the AI coach if the
scene has an `AICoach` (+ `VoiceOutput`/`VoiceInput`). Without an AI coach it just idles (no errors); call `ShowText(...)` yourself.
Debug keys (Editor only) can be switched off with the `debugKeys` box on both components. Only one orb per scene.

Glowing 7 cm orb + text beside it. World-locked at the player's eye level on their right side of the piano:
anchored to `PianoOverlay` when one is present and not calibrating (follows recalibration / grab nudges),
otherwise placed once ~0.55 m ahead / 0.4 m right of the head after tracking settles. Re-places on tracking-origin changes.
No dependency on the AI scripts.

| Member | Purpose |
|---|---|
| `void SetState(OrbState state)` | Idle (slow breathing blue) / Listening (green-cyan) / Thinking (violet↔blue shimmer) / Speaking (cyan, pulses with voice). |
| `void SetVoiceLevel(float level01)` | 0 to 1 loudness of the AI voice; drives the pulse while Speaking. Call every frame during speech (decays to 0 if not called for 0.25 s). |
| `void ShowText(string text, float seconds = -1)` | Show a message beside the orb, replacing any current one. `seconds <= 0` = auto (0.3 s/word, min 3 s). Over 120 chars is cut at a word with "...". Fades in/out. Typical use: `orb.ShowText(coachResponse.say)`. |
| `void HideText()` | Fade the message out now. |
| `void SetRevealedCharacters(int count)` / `string DisplayedText` | Show only the first `count` characters of the current text (layout doesn't reflow; `-1` = all). Used for words-in-time-with-speech. |
| `void Replace()` | Re-measure eye height and re-place on the next tracked frame. |
| `OrbState State`, `bool IsTextVisible` | Current state. |

Editor debug: `T` = sample text, `Y` = cycle state, `H` = hide text, `V` = toggle fake voice level.
Menu: **Tools ▸ Coach ▸ Add Coach Orb** (adds it to the open scene), **Tools ▸ Coach ▸ Add Aura XR Rig to Scene**,
**Tools ▸ Coach ▸ Add AI Coach + Orb Bridge**.

#### Driven by the AI: CoachOrbBridge (`Assets/Scripts/Coach/CoachOrbBridge.cs`) ✅
Glue between the AI layer and the orb; the AI scripts are not modified. Add it to any object; it finds `CoachOrb`,
`AICoach`, `VoiceOutput` and `VoiceInput` itself (only the orb is required).
- **State** (polled each frame, by priority): `VoiceInput.IsListening` → Listening, `AICoach.IsThinking` → Thinking
  (shows `"..."`), `VoiceOutput.IsSpeaking` → Speaking, else Idle.
- **Text:** `AICoach.OnResponse` → the reply appears **word by word in time with the voice** (fallback replies too; empty = nothing).
  Groq: progress = playback position of VoiceOutput's single TTS clip, weighted for pauses after `, ; :` and `. ! ?`.
  Native TTS: estimated pace (`charsPerSecond`, 14). Until Groq audio starts, the orb stays Thinking with `"..."`.
  When speech ends the whole line shows, then `lingerSeconds` (2 s) or the reading time. `revealWithSpeech` off = whole line at once.
- **Voice level:** RMS of `VoiceOutput.source` (Groq TTS) × `voiceGain`; synthetic pulse when nothing measurable plays
  on that source (Android native TTS, or the TTS request still downloading).
- Editor debug: `G` = send `AIDebugPanel.Improving()` (a fake "run finished") to the coach. `logChanges` logs state/text changes.

---

## Song database — built by Nehemiah (`Assets/Scripts/Songs/`) ✅
Songs ship inside the app (no server): one JSON file per song in `Assets/Resources/SongDB/`, converted to the AI layer's
`SongData` (Canady's `Assets/Scripts/AI/SongData.cs`: bar + beat-in-bar + length + hand + finger), so the coach can read it directly.
`Assets/Resources/SongDB/Local/` and `Assets/Music/Local/` are **gitignored** (songs that can't be in the public repo, e.g.
River Flows in You); they're still built into your own app.

| Member | Purpose |
|---|---|
| `SongData SongDatabase.Get(string id)` | Whole song (both hands). e.g. `coachContext.song = SongDatabase.Get("fur_elise")`. |
| `SongData SongDatabase.Get(string id, int firstBar, int lastBar, PianoHand? hand = null)` | Bars range (sheet-music bar numbers kept), optionally one hand. |
| `SongData SongDatabase.GetSection(string id, string sectionName, PianoHand? hand = null)` | A named section from the song's `sections`, e.g. `"A section (bars 1–8)"`. |
| `IReadOnlyList<SongEntry> SongDatabase.All` / `SongEntry Entry(id)` / `ForDifficulty(max)` | Metadata (name, composer, difficulty, order, sections, license, verified) in curriculum order. |
| `void SongDatabase.Reload()` | Re-read the JSON files (after editing them in the Editor). |
| `SongNotation.ToSongData / Validate / ParseBar / MidiOf` | Notation parsing, conversion, checks (range 21–108, bar lengths, same-key overlaps, sections). |
| `MidiSongImporter.Read / ToEntry` | Minimal .mid reader (format 0/1, no dependencies, works on IL2CPP) → SongEntry; optional "simplified: melody + bass". |
| `SongAutoPlayer` (component) | Debug: lights each song note on the `PianoOverlay` at tempo (context menu Play/Stop, or `K`). |

JSON notes, one string per bar per hand (same notation the coach reads): `"E4 D4 C4:2"` (1 beat unless `:beats`),
`"C4+E4+G4:2"` chord, `"r:0.5"` rest, `"E4@3"` finger, (`fingering` field = where the finger numbers come from / what needs review; Tools ▸ Songs ▸ Sheet Music: Show Fingering toggles them on the generated sheets), `pickupRH`/`pickupLH` for an anacrusis (stored in bar 1 with beat < 1).
Imported songs use an explicit `notes` list instead. Menu: **Tools ▸ Songs ▸ Validate All / Print All / Import MIDI Files (as written | simplified)**.

Songs now: hot_cross_buns, twinkle_twinkle, ode_to_joy, chopsticks, fur_elise (bars 1–22: 1–8 typed, 9–22 from public-domain MIDI;
sections "A section (bars 1–8)", "B section + theme return (bars 9–22)", "Bars 1–22"), the_entertainer (simplified opening, bars 1–4),
clair_de_lune (whole piece, 72 bars, simplified melody + bass from public-domain MIDI). All `"verified": false` until checked against
sheet music. Source MIDI files + licenses: `Assets/Music/SOURCES.md` (Mutopia Project, public domain). Not added: River Flows in You (local only).
Note: in Für Elise bar 8 is the 1st ending and bar 9 the 2nd ending, so playing "Bars 1–22" straight through plays both endings.

**Song count (2026-11-15): 37** — 12 beginner (difficulty 1), Ode to Joy + Chopsticks (2), Für Elise + 10 intermediate (3),
The Entertainer (4), Clair de Lune + 10 advanced (5). Intermediate/advanced = opening 16 bars from Mutopia public-domain MIDI;
see Assets/Music/SOURCES.md. `SongEntry.fingering` says which songs have fingering; most new ones are flagged for review.

**Other song models in the project (separate, not merged):** Main's lesson uses `LessonSong`/`SongLibrary.FurElise`
(`Assets/Scripts/Lesson/SongData.cs`, absolute beat + duration, right hand). Canady's curriculum loads its own songs from
`Assets/Resources/Songs/` (`CurriculumSongs`, `"note": "E5"` format). This DB lives in `Resources/SongDB/` so the two never
read each other's files, and `SongDatabase.IsSongEntry` skips files in other formats. To drive Main's evaluator from the DB, convert:
absolute beat = (bar−1)·beatsPerBar + beat − 1.

### Sheet music images (`SheetLibrary`, `Assets/Scripts/Songs/SheetLibrary.cs`) ✅
Generated from the song database (so they show exactly the DB notes, incl. simplified arrangements) by
**Tools ▸ Songs ▸ Generate Sheet Music**: `MusicXmlExporter` → MusicXML → Verovio (engraving) → resvg (PNG), see
`Tools/SheetMusic/README.md` for the one-time Python setup (private venv, no admin). Output per song in
`Assets/Resources/Sheets/<songId>/`: `page-01.png …` (A4 pages, at most 5 lines of music per page — `LINES_PER_PAGE` in `Tools/SheetMusic/render_sheets.py`; 2048 px wide, black on white; title,
composer, tempo, arrangement note, every bar numbered, fingering), `system-01.png …` (one strip per line of music, cut from
the pages; all strips of a song share one size; anything outside the line's band is whited out) and `layout.json`.
Local songs → `Sheets/Local/` (gitignored).
Imported as sprites, ASTC 4x4 on Android (~6 MB per page in memory).

| Member | Purpose |
|---|---|
| `SheetMusic SheetLibrary.Get(string songId)` | `pages` (Texture2D[], index 0 = page 1), `systems` (Texture2D[], one per line, index 0 = `layout.systems[0]`) + `layout`, or null if not generated. |
| `SheetLayout.pages` | Page files + pixel sizes. |
| `SheetLayout.systems` (`SheetSystemInfo`) | One per line: `file`, `width`/`height`, `firstBar`/`lastBar`, `barEdges` (left edge of each bar, then the right edge of the last, as 0–1 fractions of the strip width), `top`/`bottom` (highlight box, 0–1 from the top). Same shape as `SongNoteDisplay`'s `SheetSystem`; a pickup shares bar 1's slot. |
| `SheetLayout.BarRects(int bar)` | Bar rectangles (page, x, y, w, h in pixels, origin top-left). Bar 1 has two if the song has a pickup (`pickup = true`). |
| `SheetLayout.NotesAt(int bar, float beat)` | Notehead rectangles struck at that bar/beat (both hands; `hand`, `midi`). |
| `SheetLayout.NotesOf(int noteIndex)` | All noteheads of one DB note (index into `SongDatabase.Get(id).notes`), incl. tied continuations (`tied = true`). |

Lesson and Free Play display black-on-white strips as-is with a white `RawImage` tint. The guided lesson uses the sheet
assigned in Main_2. The older wait-mode scene can apply database strips through `LessonSongSource.ApplySheets`, with its
assigned per-line images as a fallback. [Aura display and sheet music](AURA_DISPLAY.md)

#### Connecting the DB to Main: `LessonSongSource` (`Assets/Scripts/Songs/LessonSongSource.cs`) ✅
Every method returns null/false when the data isn't there, so callers keep their old behaviour.

| Member | Purpose |
|---|---|
| `(int first, int last)? SectionBars(string songId, string section)` | Bars of a named section (empty name = whole song); null if unknown. |
| `SongData ForCoach(string songId, string section, PianoHand hand = Right)` | The section, one hand, from the DB (with `timeSignature`): what the coach is told about. |
| `LessonSong ForLesson(string songId, string section, float bpm, PianoHand hand = Right)` | The same section as the evaluator's monophonic `LessonSong` (top note of a chord, beats from the section's first note, pickup in bar 1). |
| `bool ApplySheets(SongNoteDisplay display, string songId, string section)` | Sets `display.sheet` to the strips containing the section's bars (`barEdges`, `top`, `bottom` from `layout.json`) and `display.sheetAspect` to the strip aspect. |

## MIDI input — `MidiPianoReader` (`Assets/Scripts/MidiPianoReader.cs`, from main) 🚧
Windows Editor/standalone via winmm.dll; Android/Aura puck via `Assets/Plugins/Android/MidiBridge.java` (android.media.midi).
| Member | Purpose |
|---|---|
| `event Action<int, int> NoteOn` | `(midiNote, velocity)` |
| `event Action<int> NoteOff` | `(midiNote)` |
| `event Action<string> ChordChanged` | Chord name + notes, or "" when released. |
| `HashSet<int> HeldNotes` | Notes currently down. |
| `bool IsConnected` | 📝 agreed, not in the code yet. |

Events must be raised on Unity's main thread. Not yet wired to `PianoOverlay`.

## LessonEngine — owner: TBD 📝 (not built)
Listens to `MidiPianoReader.NoteOn/NoteOff`, drives `PianoOverlay.SetKey`, and at the end of each phrase fills a
`PerformanceMetrics` for `AICoach.GetCoachResponse` (the `PhraseResult` below is superseded by `PerformanceMetrics`).

**Lesson progression rules:** `docs/COACH_PLAYBOOK.md` (which measures to play next, at what tempo, when to repair a weak
measure). Agreed design, not implemented yet. It needs per-measure ratings and a tempo rating per run from the LessonEngine.

| Member | Purpose |
|---|---|
| `void StartPiece(string pieceId, LessonMode mode)` | `LessonMode { Curriculum, Freeplay }` |
| `event Action<int> MeasureChanged` | `(measureIndex)`. SheetPanel listens. |
| `event Action<PhraseResult> PhraseCompleted` | AICoach listens. |
| Drives `PianoOverlay.SetKey(...)` for Next / Upcoming / Correct / Wrong. | |

`PhraseResult` (sent to the AI as JSON) — proposed:
```json
{ "pieceId": "fur_elise", "phrase": 2, "measures": [5, 8],
  "wrongNotes": [{"expected": 64, "played": 65, "measure": 6}],
  "avgHesitationMs": 820, "slowestNote": {"note": 69, "measure": 7, "ms": 2100} }
```

### CurriculumEngine (`Assets/Scripts/Curriculum/CurriculumEngine.cs`) ✅ — the guided lesson, in `Main_2`
Applies `docs/COACH_PLAYBOOK.md` (rules 1–22) in timed runs and tells the coach what happened. Contract with the judge:
`EVALUATOR_CONTRACT.md`. Song: `Assets/Resources/Songs/fur_elise.json` (both hands, 105 bars, converted from a MIDI file;
`LessonConfig.songBarCount` says how many are taught).

| Member | Purpose |
|---|---|
| `bool ReplayDemo()` | Plays the current bars again (hand being practised, at the current step's tempo); a run in progress stops and restarts after. False when idle, during the song intro, a demo, or at song complete. (Canady, OK'd by Jordan) |
| `LessonConfig.autoDemoNewMeasure` (on) | The first run of each new measure (measure 1 too) is demoed first, after the coach's line; skipped when those bars were demoed in the last 5 s (e.g. the coach asked). No demo for tempo steps, hand changes, play-throughs, repairs or retries. Plays at the current step's tempo, like every other demo. |
| `void StartLesson(LessonMode mode = Curriculum, string songId = null)` / `StopLesson()` | Start from the warm-up (or the song when `skipWarmup` is on) / stop. `songId` null keeps the current `SongId` (Für Elise by default, behavior unchanged). Another teachable song (below) is taught whole, without the scale warm-up. |
| `string SongId` | The song being taught. Teachable songs = Für Elise + database entries with `"teachable": true` (`CurriculumSongs.Teachable`, `IsTeachable`, `FindTeachable(name)`, `ForLesson(id, bars)`): currently `ode_to_joy`, `twinkle_twinkle`, `mary_had_a_little_lamb`. |
| `AppModeSwitch.RequestLearn(songId, source)` / `event LearnRequested` | "Teach me X": `AppFlow` starts the lesson on that song and swaps the sheet panel (`SongNoteDisplay.Rebuild()` after changing `sheet`). Voice: `FreePlaySheet.TryParseLearnCommand`, handled in `QuestionRouter` outside a running lesson. Songs that are not teachable are still shown by "show X". |
| `HandMode Hand` | `Right`, `Left` or `Both`: the hand(s) of the current or next run. `Stages` / `StageIndex`: the hand stages of the measure or repair block. |
| `event Action<RunInfo> OnRunStarted` | A run begins (count-in first). `RunInfo.hand`, `bars`, `tempoPct`, `notes` (only that hand's notes, each with `hand`). |
| `RunInfo.countIn`, `RunInfo.goTime` | The count-in click times and the beat the music starts (`Time.unscaledTimeAsDouble`): show 4, 3, 2, 1, then "Play!". |
| `LessonPause Pause`, `bool LastRunSilent` | `WaitingForStudent`: nothing was played for `silentRunsBeforeWaiting` (2) runs; any key (or `FastForward`) starts the run again. A silent run is rated but is not an attempt. |
| `bool ResultHeld` | True while the lesson waits to go on and the last run's result should still be shown: `LessonConfig.resultMinSeconds` (4 s) since it was rated, or the coach is still talking. `SongNoteDisplay` keeps the verdict up until it is false. |
| `bool hintsDuringRuns` | Off by default: no spoken stuck hints in the middle of a run; the coach talks between runs only. |
| `event Action<ExpectedNote> OnNextNote` | This note is due now; `null` when the run is over. |
| `event Action<ExpectedNote, int, bool> OnNotePlayed` | Live feedback: the matched (or nearest) note, the key, correct. |
| `event Action<BarRange> OnRunRated` | The run is judged. Read `LastRatings` (bar → clean / shaky / problem), `LastRightRatings`, `LastLeftRatings`, `LastTempoRating`, `LastPassed`, `LastAccuracy`. |
| `State`, `Measure`, `MeasureCount`, `CurrentRange`, `CurrentBlock`, `LeadInFirstBar`, `TempoPct` | Where the lesson is. |
| `bool SimulateRun(IDictionary<int,int> wrongNotesPerBar, float tempoRatio = 1)` | Editor testing: end the run with made-up presses. |

The coach is sent at most 16 measures of the song around the current section (the whole 105 bars would be sent with
every request otherwise).

### PerformanceEvaluator (`Assets/Scripts/Lesson/PerformanceEvaluator.cs`) ✅ — Jordan
Wait-mode judge for one right-hand section from the song database: `songId` (default `"fur_elise"`) and `songSection`
(default `"A section (bars 1–8)"`), loaded with `LessonSongSource.ForLesson` (fallback, or with `songId` empty:
`SongLibrary.FurElise`). Its song types are `LessonSong` / `LessonNote` (renamed so they don't collide with the AI layer's
`SongData` / `SongNote`).

| Member | Purpose |
|---|---|
| `void HandleNoteOn(int midi, int velocity)` | Feed a key press (subscribed to `MidiPianoReader.NoteOn` automatically). |
| `void Restart()` | Back to the first note, score cleared. |
| `LessonNote CurrentNote` / `int CurrentIndex` / `bool IsFinished` | Where the player is. |
| `event Action Restarted` | A new attempt started. |
| `event Action<NoteResult> NoteJudged` | The correct key was pressed. |
| `event Action<int, int> WrongNotePlayed` | `(expectedMidi, playedMidi)`. |
| `event Action<RunStats> SongFinished` | The run summary in the coach's own type (`CoachContext.thisRun`): accuracy, timing, `tempoRating`, wrong notes as MIDI numbers, rushed / dragged bars. |

Editor debug: `Space` = correct note, `X` = wrong note, `P` = auto-play, `Backspace` = restart.

## AICoach — `AICoach` (`Assets/Scripts/AI/AICoach.cs`, from main) 🚧
Takes `PerformanceMetrics` (`Assets/Scripts/AI/PerformanceMetrics.cs`) instead of the proposed `PhraseResult`. Keys live in a
gitignored `Assets/StreamingAssets/secrets.json`.
| Member | Purpose |
|---|---|
| `Task<CoachResponse> GetCoachResponse(PerformanceMetrics m)` | One short spoken coaching line plus an optional action. |
| `Task<string> GetFeedback(PerformanceMetrics m)` | Text-only feedback. |
| `Task<CoachResponse> Ask(string question, PerformanceMetrics m = null)` | Free-form question to the coach. |
| `event Action<CoachResponse> OnResponse` | Raised with each response. |
| Drives `CoachOrb.SetState(...)` / `SetVoiceLevel(...)` | 📝 not yet (CoachOrb not built). |

`PerformanceMetrics` (filled by the LessonEngine, sent to the coach as JSON):
```csharp
public class PerformanceMetrics {
    string songName; List<WrongNote> wrongNotes;   // WrongNote { int bar; string expectedNote; string playedNote; } e.g. "E4"
    float avgTimingErrorMs; List<int> rushedBars; List<int> draggedBars; float accuracyPercent; // 0-100
}
```
`CoachResponse { string say; string action; int bar; }`; `action` = `loop_bar` / `slow_down` / `next_song` / `none`
(`CoachAction { None, LoopBar, SlowDown, NextSong }`). The LessonEngine acts on it (e.g. loop bar N).

Voice and config:
| Member | Purpose |
|---|---|
| `VoiceInput.StartListening()` / `StopListening()` | Push-to-talk. On release the clip goes to Groq Whisper. |
| `event Action<VoiceCommand, string> VoiceInput.OnCommand` | `VoiceCommand { Again, Slower, HowDidIDo, NextSong, Unknown }` + transcript. |
| `Task VoiceOutput.Speak(string text)` / `Stop()` / `IsSpeaking` | Speaks a line (Groq TTS or native Android TTS); a new call cuts off the current one. |
| `AIConfig` (`Assets/Resources/AIConfig.asset`, `AIConfig.Get()`) | Provider (`Claude`/`Groq`), models (coach `claude-haiku-5-5`), TTS (`Groq`/`Native`), timeouts, fallback lines, system prompts. |

Editor test: `AIDebugPanel` in the `AITest` scene.

### Hands-free questions ("hey teacher") — Canady (`Assets/Scripts/AI/`, `Assets/Plugins/Android/WakeWordBridge.java`)
Added at runtime to any scene with an `AppFlow` (no scene edit; off with `AIConfig.wakeWordEnabled`). Armed only in
`AppFlow.Step.Lesson` while the coach isn't thinking/speaking (+`muteAfterSpeechSeconds`), and not during VoiceInput listening.
Flow: trigger → chime → record question (VAD end-of-speech) → Groq Whisper → `AICoach.Ask(question, ctx)` with the song,
`currentBar` and the last `RunStats`.
| Member | Purpose |
|---|---|
| `IVoiceTrigger { Name; IsActive; SetArmed(bool); event Action<IVoiceTrigger> Triggered }` | A way to start a question. |
| `IQuestionRecorder { IsRecording; BeginQuestion(); Cancel(); event Action<byte[]> QuestionRecorded }` | Records one question; WAV bytes or null if nothing was heard. |
| `WakeWordTrigger` (both interfaces) | sherpa-onnx keyword spotter via `com.canady.wakeword.WakeWordBridge`; owns the mic while running (Android only). Any plugin error disables it for the session. |
| `BackupVoiceTrigger` | **Q** key, or right-hand pinch held `pinchHoldSeconds` (0.6) with the palm NOT down; ignored while `PianoCalibrator.IsCalibrating` and on the system gesture. |
| `MicQuestionRecorder` | Unity `Microphone` recorder with the same VAD; used in the Editor and when the plugin is unavailable. |
| `QuestionRouter.IsListening` / `IsTranscribing` | Read by `CoachOrbBridge` → orb Listening / Thinking. |
| `CoachContext.currentBar` | Bar the player is on (`"current_bar"` in the JSON, only when > 0). |
| `AIConfig` "Wake word" section | Score/threshold, VAD, cooldown, mute, audio source (`voiceCommunicationSource`), backup inputs, `saveMicDebugClip`. |
Java bridge methods: `setTuning`, `setVad`, `setAudio`, `start`, `stop`, `pause`, `resume`, `suspendMic`, `resumeMic`,
`beginQuestion`, `cancelQuestion`, `saveDebugClip(path, seconds)`; callbacks `OnWakeWord`, `OnQuestionAudio(path)`,
`OnWakeWordStatus`, `OnWakeWordError`. Editor keys: **Q** ask, **F11** save a 10 s mic clip (F9 is visuals on/off). Logcat: `adb logcat -s Unity WakeWord`.

### Non-visual mode — Canady (`Assets/Scripts/AI/VisualsMode.cs`, `VisualsGate.cs`) 🚧 (Editor)
Nothing of ours is drawn: `VisualsGate` sets the main camera's culling mask to 0, so every module keeps its state (the next key stays
lit underneath) without knowing. Switch: "hey teacher, visuals off / on" (also through the pinch-hold backup trigger), the radial
menu's **Visuals Off** (visual → non-visual only; the menu never opens while non-visual), Editor **F9**. "Visuals on" when the piano
was never calibrated runs calibration first (`visualsCalibrateFirstLine`). `ModeChoicePanel` ignores pokes while non-visual.
| Member | Purpose |
|---|---|
| `VisualsMode.NonVisual` | True while nothing is drawn. Starts from `AIConfig.nonVisualDefault` (set before any `Start`). |
| `VisualsMode.Set(bool nonVisual, string source)` / `Toggle(source)` | Switch; raises `Changed(nonVisual, source)`. |
| `VisualsMode.TryParseCommand(text, out bool visualsOn)` | "visuals off", "turn on the visuals", "audio only", ... (EditMode tests). |
| `AIConfig` "Non-visual" section | `nonVisualDefault`, `nonVisualChooseModeLine`, `visualsCalibrateFirstLine`, `visualsOffLine`. |
A non-visual start (`nonVisualDefault`) skips calibration: `AppFlow` goes from the intro to the mode choice by voice
(`nonVisualChooseModeLine`). Without sight: the intro answer "audio only" (`VisualsMode.AsksForNonVisual`, hint
`audioOnlyIntroHint`), or "hey teacher, visuals off" during the first calibration (cancels it). `NonVisualGuide` speaks the
next run's notes (`AICoach.LineSuffix`), "No, E: ..." on a wrong key and the note + where it is after 2.5 s stuck (Main_2).
| `AICoach.LineSuffix` | `Func<CoachResult, (string text, IReadOnlyList<AudioClip> clips)>`: text appended to the coach line, or clips played right after it. |
| `VoiceOutput.Speak(text, clips, gap, clipPitch)` | Speaks the line, then the clips; `IsSpeaking` stays true until they end. |
| `SpokenWords.Preload()` / `Get(words)` | Cached TTS clips for the 12 note names, "finger one".."five", "Right/Left hand"; `Get` is null if any is missing. |
| `GroqTts.Synthesize(text, keepAfter)` | One TTS clip without playing it. |

### Free Play — Canady (`Assets/Scripts/FreePlay/`)
The player plays anything; pressed keys light up (`KeyState.Correct`); the coach comments now and then and answers "hey teacher"
questions. Code computes every musical fact (`FreePlayAnalyzer`, pure C#, EditMode tests in `FreePlay/Editor/`); the AI only words them.
| Member | Purpose |
|---|---|
| `enum AppMode { Lesson, FreePlay }`, `enum StartMode { Ask, Lesson, FreePlay }` | Modes; `AIConfig.defaultMode` (Ask = show the panel). |
| `AppModeSwitch.Request(mode, source)` / `event Requested` / `Current` / `AfterCalibration` | Panel and voice ask, AppFlow switches. |
| `FreePlayController.Begin()` / `End()` / `IsActive` / `Feed(midi, vel, on)` / `FactsJson(forQuestion)` / `event PhraseFinished` | Runs free play from `MidiPianoReader.NoteOn/NoteOff`. `End()` clears every key it lit. |
| `ModeChoicePanel.ShowChoice()` / `ShowSwitchButton()` / `Hide()` / `event Chosen` | Legacy scenes build the world-space mode buttons. Main_2 uses the tracked hand menu and closes it when the mode changes. |
| `FreePlayAnalyzer` (`NoteOn`, `NoteOff`, `Poll(now)`, `Peek(now)`, static `IdentifyChord`, `EstimateKey`, `RateSteadiness`, `MatchSong`, `RomanNumeral`) | Phrases (2 s gap), chords within 80 ms (inversions), Krumhansl-Schmuckler key + confidence, tempo steadiness (CV), song match vs `SongDatabase` openings (top voice, transposition-invariant, ≥ 6 notes + distinctiveness guard), range, likely hands. |
| `FreePlayFacts.Build(...)` / `IsNotable(...)` | Facts JSON for the coach; `can_learn_in_lesson` only for the evaluator's `songId` (today Für Elise). |
| `CoachTrigger.PhraseEnded`, `CoachContext.freePlayJson`, mode `"free_play"` | Unprompted comment only on a recognized song or a new key; 40 s cooldown (`AIConfig.freePlayCommentCooldownSeconds`). One sentence, never praise. |
| `CoachPolicy.Decide/PraiseAllowed/Cue/Tidy/RepeatsOpening` | Which coach turns reach the model. Passed lesson runs (tempo step, measure learned, hand change, warm-up) get a code cue of ≤5 words ("Now bar 3.", "Now the left hand.") or nothing when only the speed changes; failed runs, lesson start, help, play-throughs, song complete and questions go to the model. Praise only on a full run: Main's whole excerpt (`freeplay` RunFinished), `PlayThroughPassed`, `SongComplete`. Replies are cut to one sentence ≤80 chars (two ≤180 for questions) and lose filler openers ("Nice try, but"). |
| `CoachContext.praiseAllowed` / `recentLines` | Sent as `praise_allowed` and `do_not_reuse` (last 5 lines). A reply whose first 4 words match one of them is retried once, then replaced by the cue or skipped. |
| `LessonWalkthrough.Run(n, realTime)` (Editor only) | Play-mode driver for Main_2: passes every run up to measure n and logs each coach line as `[Walkthrough]`. `realTime` keeps speech on and lets each count-in play first (logs its tempo). |
| `PassCues.For(ctx, cue)` / `AIConfig.passCues`, `passCueVolume` (0.2) | Lesson-only sounds, no speech or model call: soft ding (150 ms) for a pass that only speeds up, two-note G4→C5 (300 ms) for a new hand or new bars, three-note C5-E5-G5 (540 ms) for a measure learned, play-through or song complete. Low plucked tones, unlike the high flat "hey teacher" chime. Played after any speech, and `AICoach.Coach` returns only once it ends, so the next count-in (already at the new tempo) and the spoken line come after it. |
| `QuestionRouter.TryParseModeCommand(text, out mode)` | "hey teacher, free play" / "lesson mode" switch modes instead of asking the coach. |
| `FreePlaySheet` (added at runtime by `FreePlayController`; `Show(songId, byVoice)`, `Hide()`, `CurrentBar`) | Free play sheet music: a database song's generated strips (`SheetLibrary`) in the lesson sheet's spot (reads `SongNoteDisplay` transform/`panelSize`/`metersPerUnit` only), one line at a time, the bar being played outlined (`SheetFollower`: lenient, wrong notes ignored, re-syncs on 4 notes). Opens on a recognized song or by voice; a voice pick stays until closed. Hidden when free play ends. No lit keys or judging. |
| `FreePlaySheet.TryParseShowCommand` / `IsCloseCommand` / `FindSong`, `AIConfig.noSheetLine` | "hey teacher, show (me the music for) Jingle Bells" / "close the music", free play only, no model call. Song names matched against `SongDatabase.All` (accents/punctuation ignored, one letter off allowed); a "show ..." naming no song goes to the coach as a question. |
| `QuestionRouter.IsReplayCommand(text)`, `AIConfig.cantReplayLine` | "hey teacher, play it again / play that again / show me again": no model call, calls `CurriculumEngine.ReplayDemo()`; says `cantReplayLine` when there's nothing to replay (Main has no demo player). |
| `QuestionRouter.IsYes(text)`, `AIConfig.confirmFreePlayLine` / `keepLessonLine` | "free play" said during the lesson first asks "Do you want to switch to free play?" (once the coach is free), then listens without a wake word: yes switches; no, anything else or silence keeps the lesson. "lesson mode" and the panel switch at once. |
Editor: `FreePlayDebugPanel` (OnGUI buttons: Start Free Play / Back to Lesson, C–F–G–C, Für Elise, Uneven run). Config: `AIConfig` "Free play" + "Mode".

---

## Radial menu — built by Nehemiah (`Assets/Scripts/Menu/RadialMenu.cs`) ✅ (Editor) / untested on the Aura
Main_2 uses the tracked right index only. Touch the center on the left hand, swipe toward a colored action, then lift.
A swipe requires at least 5 cm of right-index travel and 5 cm of separation from the center in the action's direction.
Return to the center before lifting to abandon the swipe. Tap **Close** or lower the hand to collapse the ring.
Restart Lesson and Recalibrate reveal a tinted **Are you sure?** branch as soon as the swipe reaches the action.
Continue outward through the branch to about 9 cm before withdrawing, or start another swipe from center if you lifted early.
The ring and confirmation follow the left hand. The Fingering sector reads **Lesson only** before entering Lesson.
Fine Tune opens the existing calibration panel. Fingering is available in Lesson. The Main_2 wrist menu exposes no XRI ray or poke targets.
Legacy scenes retain their original orange-dot menu. Editor: **F1** toggles the ring with a simulated hand if needed, and **Esc** closes it.

| Member | Purpose |
|---|---|
| `enum Choice { RestartLesson, FreePlay, Recalibrate, Close, Fingering, FineTune, Visuals }` | Fingering uses the guide's device-local preference. Fine Tune opens the marker offsets. Visuals: `VisualsMode.Set(true, "menu")`; the menu never opens while non-visual (six buttons, 60° apart; the fallback ring puts Visuals at the bottom). |
| `void Open()` / `Close()` / `Toggle()`, `bool IsOpen` | Show/hide. Opening never changes the session. |
| `bool Choose(Choice)` | Request an action. Main_2 opens a confirmation for Restart Lesson and Recalibrate. False if unavailable or an action already ran. |
| `event Action<Choice> Chosen` | After an action runs. |

Actions (public APIs only): Restart = in the lesson `overlay.ClearAll()` + `evaluator.Restart()` (Main) or `engine.StartLesson()` (Main_2);
outside it `AppModeSwitch.Request(Lesson, "menu")` (shown as "Start Lesson"). Free Play = `AppModeSwitch.Request(FreePlay, "menu")`
(disabled while already in free play). Recalibrate = `calibrator.Recalibrate()`; in Main's wait-mode lesson the evaluator component is
switched off until `Calibrated`/`CalibrationCancelled` (note index kept). While calibrating, the option becomes **Cancel Recalibrate**
(`CancelCalibration()`), only if an earlier calibration exists. Nothing is available before the first calibration.
**Not done:** the guided lesson (`CurriculumEngine`, Main_2) has no pause API, so its runs are not paused during recalibration.

## Requested (add asks for other owners here)
- **PianoOverlay — Ari:** a `KeyState.Pressed` color (held key, no flash). Free Play lights held keys with `Correct` and re-lights
  them every half flash; a steady Pressed state would be cleaner. Not blocking.
- **SongFormatter — Nehemiah (song DB):** the coach's song text drops rests and pickups (Für Elise bar 1 shows the pickup E5 D#5 as
  its first notes; "A4 r C4" reads as "A4 C4"). Consider printing rests (`r:0.5`) and the pickup separately. Not blocking.
- **Song model — Nehemiah → Jordan + Canady:** two `SongNote` classes (Lesson vs AI) can't coexist; agree on one (the song DB uses
  Canady's `SongData`) before merging jordan-branch.
- **AICoach / VoiceOutput — Nehemiah (CoachOrbBridge):** (1) events for speaking started/finished and thinking started/finished,
  so the orb doesn't have to poll `IsSpeaking`/`IsThinking`; (2) a way to tell when TTS audio actually starts playing
  (`IsSpeaking` is already true while the Groq request downloads, so the orb pulses before any sound); (3) optional: a 0..1
  voice level for native Android TTS (it doesn't play through `VoiceOutput.source`, so the orb fakes the pulse). Nice-to-haves, not blocking.
- _example: "SheetPanel needs measure-to-page mapping from LessonEngine — Ari"_
- LessonEngine needs an owner: wait mode (MIDI → `PianoOverlay.SetKey`), scoring, `PerformanceMetrics` for the coach.
- MIDI → overlay wiring (light the pressed key): on hold until Ari says go.
- `MidiPianoReader.IsConnected` (agreed above, not in the code yet) — for Jordan.
- AICoach → `CoachOrb` state + voice level, once CoachOrb exists — Ari + Canady.
- Move `MidiPianoReader` / `MidiLogUI` into `Assets/Scripts/Midi/` to match the folder convention — Jordan.
