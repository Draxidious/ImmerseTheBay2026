# Piano Pal

Built for **Immerse the Bay 2026**.

An AR piano teacher for **XREAL Aura** (Android XR, optical see-through glasses). You sit at a real
**Yamaha P71** digital piano. The glasses overlay which key to press, show the sheet music on a floating
panel, and an AI coach gives short spoken feedback between phrases. Everything runs on the Aura puck,
with no laptop or server in the loop.

## How it works

```
Yamaha P71 ──USB MIDI──► Aura puck ──► Unity app
                                        ├─ MidiPianoReader       (Android MidiManager bridge → C# events)
                                        ├─ AppFlow               (startup, calibration, overlay, coach wiring)
                                        ├─ PerformanceEvaluator  (wait mode, scoring, run stats)
                                        ├─ UI                    (key overlay, sheet music, coach orb)
                                        └─ AICoach               (LLM + speech in/out, between phrases only)
```

- **Lesson flow:** intro → corner-marker calibration → choose lesson or free play. The lesson teaches
  Für Elise (both hands, 105 bars) measure by measure: right hand, left hand, both hands, then up a tempo
  ladder, followed by play-throughs and repairs. See [docs/COACH_PLAYBOOK.md](docs/COACH_PLAYBOOK.md).
- **Calibration:** ArUco markers behind A0 and C8 align the virtual keyboard to the real one. A four-pinch
  fallback is used if markers can't be tracked.
- **The AI is never in the real-time loop.** Note matching, highlighting and advancing work instantly
  offline. The AI only receives a short performance summary and speaks between phrases, and it never
  generates notes.
- **Left hand:** amber keys. **Wrist menu** on the left hand offers Fine Tune, Recalibrate, Restart Lesson
  and Fingering.

## Requirements

- **Unity 6000.6.8f1** (everyone must use exactly this version)
- Android build target with OpenXR + Android XR, XR Interaction Toolkit and XR Hands
- XREAL Aura + puck, Yamaha P71 connected to the puck over USB MIDI
- Optional: Anthropic and Groq API keys for the coach

## Getting started

1. Open the project in Unity 6000.6.8f1 and let it import.
2. Open `Assets/Scenes/Main_2.unity`. This is the main scene and the only enabled build scene.
3. Copy `Assets/StreamingAssets/secrets.example.json` to `secrets.json` (gitignored) and add
   `ANTHROPIC_API_KEY` / `GROQ_API_KEY`. Without keys, the coach uses canned feedback and native TTS.
4. Build: Build Profiles → Android → Run Device = puck → **Build And Run**.

> **Never share an APK built with real keys.** Files in `StreamingAssets` are packed into the APK, and
> `secrets.json` must never be committed.

### Editor testing (no piano or headset)

| Key | Action |
|---|---|
| Enter | Skip speech / listen waits |
| M | Simulate a marker lock |
| C | Simulate pinch calibration |
| F1 | Toggle the hand menu |
| F5–F8 | End a lesson run: clean / shaky / problem / too slow |

`Tools → Piano → Run Fingering Smoke Test` checks the hand menu and lesson flow.

## Project layout

| Path | Contents |
|---|---|
| `Assets/Scenes/` | `Main_2.unity` (main scene), `Main.unity` (older, disabled), per-person dev scenes |
| `Assets/Scripts/` | MIDI input, `App/` (AppFlow), `Lesson/` (curriculum, evaluator), `UI/`, `AI/` |
| `Assets/Resources/Songs/` | Song data as JSON (`fur_elise.json`) |
| `Assets/Music/` | Sheet music images |
| `Assets/Plugins/Android/` | Android MIDI bridge |
| `docs/` | Interfaces, coach playbook, Aura display notes, device test checklist, devlog |

## Documentation

- [CLAUDE.md](CLAUDE.md) — project rules, conventions and Aura/Unity gotchas (read first)
- [docs/INTERFACES.md](docs/INTERFACES.md) — public methods and events between modules
- [docs/COACH_PLAYBOOK.md](docs/COACH_PLAYBOOK.md) — lesson progression and coach actions
- [docs/AURA_DISPLAY.md](docs/AURA_DISPLAY.md) — optics, passthrough and sheet music on the glasses
- [docs/AURA_MAIN_TEST.md](docs/AURA_MAIN_TEST.md) — full on-device checklist and adb commands
- [docs/HANDOVER_MARKER_CALIBRATION.md](docs/HANDOVER_MARKER_CALIBRATION.md) — marker calibration
- [docs/DEVLOG.md](docs/DEVLOG.md) — dated change log

## Contributing

Notes are MIDI numbers everywhere (middle C = 60, P71 range 21–108) and units are meters. Stay inside
your own module's folder, and update `docs/INTERFACES.md` and `docs/DEVLOG.md` when you change anything
other modules use.

## License

MIT, see [LICENSE](LICENSE). Third-party components are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
