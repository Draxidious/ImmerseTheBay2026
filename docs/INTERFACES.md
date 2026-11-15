# INTERFACES.md — public hooks between modules

The contract between modules. If it isn't listed here, don't depend on it.
**Update this file in the same commit as any change to a public API.**

Status legend: ✅ implemented · 🚧 in progress · 📝 agreed, not built yet

---

## Shared types

```csharp
public enum KeyState { Off, Upcoming, Next, Correct, Wrong }   // ✅ defined in PianoOverlay.cs
public enum OrbState { Idle, Listening, Thinking, Speaking }   // 📝 CoachOrb.cs
```

MIDI note numbers throughout: middle C = 60, P71 range = 21 (A0) to 108 (C8).

---

## UI — owner: Ari

### PianoOverlay (`Assets/Scripts/UI/PianoOverlay.cs`) ✅
88-key overlay generated in code. Local frame: +X along the keyboard (left to right), +Z toward the fallboard, +Y up.

| Member | Purpose |
|---|---|
| `void SetKey(int midiNote, KeyState state)` | Set one key's state. `Correct`/`Wrong` auto-revert to `Off` after `flashSeconds`. |
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

Keys never have colliders. The only grabbable part is the `GrabHandle` child (an `XRGrabInteractable` on the overlay root, position-only).

### PianoCalibrator (`Assets/Scripts/UI/PianoCalibrator.cs`) ✅
Hand-tracking calibration (XR Hands thumb-tip/index-tip distance). Default is four pinches (`fourPointCalibration`):
far-left key's FAR end (at the panel) then its NEAR end, then the same for the far-right key; either order per key is accepted.
Calls `PianoOverlay.CalibrateFromFourPoints`. Shows a marker at each pinch and a floating prompt; every pinch is logged
(`[PianoCalibrator] Pinch for step …`, position relative to the head). Disables the overlay grab while calibrating; saves the
overlay pose when the grab is released.

Hand occlusion: after calibration the tracked hand meshes (`handMeshRenderers`) get the depth-only material
`PianoTeacher/HandOccluder` (`Resources/HandOccluder`, every material slot replaced, mesh grown 4 mm), so overlay pixels behind
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
| `event Action Calibrated` | Raised after a successful calibration. |
| `bool IsCalibrating` / `Phase CurrentPhase` | `Phase { Idle, WaitingForLow, WaitingForHigh }`. |

Editor debug: `C` = fake both pinches in front of the camera, `R` = recalibrate.

### SheetPanel (`Assets/Scripts/UI/SheetPanel.cs`) 📝
| Member | Purpose |
|---|---|
| `void LoadPiece(string pieceId)` | Load the page images for a piece from `Assets/Music/<pieceId>/`. |
| `void HighlightMeasure(int measureIndex)` | Highlight the current measure; turns the page if needed. |
| `Vector3 GetNoteWorldPosition(int measureIndex, int noteIndex)` | (stretch) For drawing a line from a note to its key. |

### CoachOrb (`Assets/Scripts/UI/CoachOrb.cs`) 📝
| Member | Purpose |
|---|---|
| `void SetState(OrbState state)` | Idle / Listening / Thinking / Speaking visuals. |
| `void SetVoiceLevel(float level01)` | 0 to 1 loudness of the AI voice; drives the pulse while Speaking. Call every frame during speech. |

---

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

---

## Requested (add asks for other owners here)
- _example: "SheetPanel needs measure-to-page mapping from LessonEngine — Ari"_
- LessonEngine needs an owner: wait mode (MIDI → `PianoOverlay.SetKey`), scoring, `PerformanceMetrics` for the coach.
- MIDI → overlay wiring (light the pressed key): on hold until Ari says go.
- `MidiPianoReader.IsConnected` (agreed above, not in the code yet) — for Jordan.
- AICoach → `CoachOrb` state + voice level, once CoachOrb exists — Ari + Canady.
- Move `MidiPianoReader` / `MidiLogUI` into `Assets/Scripts/Midi/` to match the folder convention — Jordan.
