# XREAL Aura display and sheet music

## What Main_2 renders

**Lesson and Free Play render standard black notation on white sheet-music strips.** Guided Lesson uses the score images
assigned in `Main_2`, including the original Für Elise JPEG strips. Free Play loads generated PNG strips. Both use a Unity
`RawImage` with a white tint. Bar highlights are rendered separately. Keep the source notation polarity when regenerating
the sheets. The app displays the panel without an inversion pass or a sheet-specific lens-dimming change.

The Lesson panel is implemented by [SongNoteDisplay](../Assets/Scripts/Lesson/SongNoteDisplay.cs), and the Free Play
panel by [FreePlaySheet](../Assets/Scripts/FreePlay/FreePlaySheet.cs). The [sheet generation pipeline](../Tools/SheetMusic/README.md)
produces the white-page PNGs. The [device checklist](AURA_MAIN_TEST.md) covers readability in the glasses.

## Optics, lens dimming, and capture

The Aura is optical see-through. The wearer sees the real room directly through the lenses. The display contributes
light, so in additive viewing a dark mark contributes less light than its bright surroundings. A bright virtual sheet
can therefore provide contrast around dark notation even though ambient light still reaches the eye. Contrast varies
with the environment and the lens setting. XREAL recommends checking the app at different ambient light levels and
electrochromic settings. [XREAL Aura app design guidelines](https://tutorials.xreal.com/docs/aura/developers/app-design-guidelines/) ·
[Android XR visual design](https://developer.android.com/design/ui/xr/guides/visual-design)

Electrochromic dimming changes the amount of real-world light passing through the lenses across the view. XREAL
documents five transmittance levels and the glasses' X button for changing levels. Android XR also documents a
global dimming API for Unity. A dimming setting is separate from the sheet texture's colors and Unity material alpha.
[XREAL Aura FAQ](https://tutorials.xreal.com/docs/aura/troubleshooting/faq/) ·
[Android XR for Unity](https://developer.android.com/develop/xr/unity)

Judge how the physical keyboard and sheet look while wearing the glasses. Review the recording separately because it uses
a digital composition. For example, an opaque black material can look dark in a recording while contributing no display
light in additive viewing. The material and render path determine alpha. Where the scene needs empty space, omit the
geometry or use the appropriate transparent render path. [Android XR display blend modes](https://developer.android.com/develop/xr/jetpack-xr-sdk/display-blend-mode)

## Unity configuration for this project

- `Main_2` has an `ARSession` and an enabled `ARCameraManager` on Main Camera. The camera clears to solid transparent black
  `(0, 0, 0, 0)`. The Android OpenXR configuration enables the Android XR session and camera features. The Unity package
  uses the camera feature for the runtime's passthrough support. [Android XR for Unity](https://developer.android.com/develop/xr/unity)
- During the November 14 on-device test, the affected build reported `OPAQUE` and the room looked blacked out. After
  the Unity configuration was fixed, that build reported `ALPHA_BLEND` and the room was visible. Those were observations
  on the tested runtime. If the room disappears again, inspect the runtime mode and the lens setting independently.
  [Device test history](DEVLOG.md)
- Check the sheet's notes, staff lines, and bar highlight against the real backdrop in normal playing conditions, and
  check the captured demo video on its own. The wearable menu and keyboard overlay are separate from the sheet renderer.
