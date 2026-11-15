# INTERFACES.md — public hooks between modules

The contract between modules. If it isn't listed here, don't depend on it.
**Update this file in the same commit as any change to a public API.**

Status legend: ✅ implemented · 🚧 in progress · 📝 agreed, not built yet

---

## Shared types

```csharp
public enum KeyState { Off, Upcoming, Next, Correct, Wrong }   // ✅ defined in PianoOverlay.cs
public enum OrbState { Idle, Listening, Thinking, Speaking }   // ✅ defined in Assets/Scripts/Coach/CoachOrb.cs
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
| `void SaveCalibration()` | Persist the current pose/scale to PlayerPrefs. Called automatically after calibration and after a grab-handle nudge. |

Editor debug: `1` = Next on C4 with Upcoming E4/G4, `2` = Correct C4, `3` = Wrong C#4, `0` = clear.

`CalibrateFromTwoKeys` keeps the keyboard's tilt along its length (up to `maxTiltDegrees`, default 5°); front-to-back stays level.
The calibrator's post-calibration preview only clears keys it lit itself, so lesson highlights set meanwhile are kept.

Keys never have colliders. The only grabbable part is the `GrabHandle` child (an `XRGrabInteractable` on the overlay root, position-only).

### PianoCalibrator (`Assets/Scripts/UI/PianoCalibrator.cs`) ✅
Two-pinch hand-tracking calibration (XR Hands thumb-tip/index-tip distance). Pinch 1 = front-edge center of the lowest key,
pinch 2 = the highest key, then calls `PianoOverlay.CalibrateFromTwoKeys`. Shows a marker at each pinch and a floating prompt.
Disables the overlay grab while calibrating; saves the overlay pose when the grab is released.

| Member | Purpose |
|---|---|
| `void Recalibrate()` | Forget the points and wait for two new pinches. Also wired to the `RecalibrateButton` interactable. |
| `void SubmitPoint(Vector3 worldPoint)` | Feed a calibration point manually (non-hand input, tests). Rejects spans outside 60–160% of `ExpectedSpanMeters()` and swaps right-then-left input. |
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
Songs ship inside the app (no server): one JSON file per song in `Assets/Resources/Songs/`, converted to the AI layer's
`SongData` (Canady's `Assets/Scripts/AI/SongData.cs`: bar + beat-in-bar + length + hand + finger), so the coach can read it directly.
`Assets/Resources/Songs/Local/` and `Assets/Music/Local/` are **gitignored** (songs that can't be in the public repo, e.g.
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
`"C4+E4+G4:2"` chord, `"r:0.5"` rest, `"E4@3"` finger, `pickupRH`/`pickupLH` for an anacrusis (stored in bar 1 with beat < 1).
Imported songs use an explicit `notes` list instead. Menu: **Tools ▸ Songs ▸ Validate All / Print All / Import MIDI Files (as written | simplified)**.

Songs now: hot_cross_buns, twinkle_twinkle, ode_to_joy, chopsticks, fur_elise (bars 1–22: 1–8 typed, 9–22 from public-domain MIDI;
sections "A section (bars 1–8)", "B section + theme return (bars 9–22)", "Bars 1–22"), the_entertainer (simplified opening, bars 1–4),
clair_de_lune (whole piece, 72 bars, simplified melody + bass from public-domain MIDI). All `"verified": false` until checked against
sheet music. Source MIDI files + licenses: `Assets/Music/SOURCES.md` (Mutopia Project, public domain). Not added: River Flows in You (local only).
Note: in Für Elise bar 8 is the 1st ending and bar 9 the 2nd ending, so playing "Bars 1–22" straight through plays both endings.

**Not merged with Jordan's model:** jordan-branch's `Song`/`SongNote` (`Assets/Scripts/Lesson/SongData.cs`, absolute beat + duration,
one hand) clashes by name with Canady's `SongNote`. The DB uses Canady's `SongData`; Jordan's `PerformanceEvaluator` would need a
converter (absolute beat = (bar−1)·beatsPerBar + beat − 1) once the two models are reconciled.

## MIDI input — owner: TBD 📝
| Member | Purpose |
|---|---|
| `event Action<int, int> NoteOn` | `(midiNote, velocity)` |
| `event Action<int> NoteOff` | `(midiNote)` |
| `bool IsConnected` | Whether a MIDI device is attached. |

Events must be raised on Unity's main thread.

## LessonEngine — owner: TBD 📝
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

## AICoach — owner: TBD 📝
| Member | Purpose |
|---|---|
| `void OnPhraseCompleted(PhraseResult r)` | Builds a prompt and speaks 1 to 2 sentences of feedback. |
| Drives `CoachOrb.SetState(...)` and `CoachOrb.SetVoiceLevel(...)`. | |

---

## Requested (add asks for other owners here)
- **SongFormatter — Nehemiah (song DB):** the coach's song text drops rests and pickups (Für Elise bar 1 shows the pickup E5 D#5 as
  its first notes; "A4 r C4" reads as "A4 C4"). Consider printing rests (`r:0.5`) and the pickup separately. Not blocking.
- **Song model — Nehemiah → Jordan + Canady:** two `SongNote` classes (Lesson vs AI) can't coexist; agree on one (the song DB uses
  Canady's `SongData`) before merging jordan-branch.
- **AICoach / VoiceOutput — Nehemiah (CoachOrbBridge):** (1) events for speaking started/finished and thinking started/finished,
  so the orb doesn't have to poll `IsSpeaking`/`IsThinking`; (2) a way to tell when TTS audio actually starts playing
  (`IsSpeaking` is already true while the Groq request downloads, so the orb pulses before any sound); (3) optional: a 0..1
  voice level for native Android TTS (it doesn't play through `VoiceOutput.source`, so the orb fakes the pulse). Nice-to-haves, not blocking.
- _example: "SheetPanel needs measure-to-page mapping from LessonEngine — Ari"_
