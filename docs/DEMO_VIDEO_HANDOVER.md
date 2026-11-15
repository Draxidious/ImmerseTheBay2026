# Piano Pal demo video handover

**Team deliverable:** One 30-second vertical MP4 for Devpost. Aim for 1080 × 1920 at 30 fps. Pens down Sunday, Nov 15 at 9:00 a.m. PT. The submission also needs the public Git repo.

We need to show someone wearing XREAL Aura and using Piano Pal on our real Yamaha P71. The Devpost story gives us the demo arc: follow a cue on the physical piano, play the note, then hear how the coach responds to the performance. Keep the press and its app response together in the same take.

## Who can grab what

- **On-camera teammate:** Film the glasses pickup and put-on vertically. Then wear the Aura for a clean guided run and a Free Play take.
- **Aura capture teammate:** Start and stop wireless capture while the wearer plays. Get a short alignment test first. Grab a vertical phone angle over the shoulder as backup and record the room audio so we have the real piano.
- **Assembly teammate:** Record the scripted voiceover, cut the footage to the beats below, add the tile transition and captions, and export the MP4 with the cyan chamfer logo.
- **Submission teammate:** Watch the exported file on a phone with sound, confirm the runtime and readability, and check the Devpost video and public repo links before the cutoff.

## Shoot-ready 30-second script

| Time | What we see | What we hear |
| --- | --- | --- |
| 0:00–0:03 | Vertical close-up. The player picks up the XREAL Aura and puts it on. Falling piano tiles fill the frame as the glasses settle, then cut into POV. A quick fade to black is the backup. | Busy room sound drops away as the glasses go on. Start **VO 1** just before the cut. |
| 0:03–0:07 | Reveal the real keyboard and floating Für Elise score. If the camera makes the overlay hard to read, cut between the cleanest score capture and a matching shot of the real keyboard. | **VO 1:** “What if you could take your piano instructor everywhere you go?” |
| 0:07–0:14 | Guided Lesson. Show the active measure and a numbered fingertip, then hold on the target key being pressed and flashing green. | **VO 2:** “With Piano Pal, follow the score and see which key to press.” Leave a beat for the real note. |
| 0:14–0:20 | Cut to the end of that phrase. Show the coach orb speaking and the player preparing for another try. | **Live coach audio.** Use one real line about what to work on next, ideally 2–4 seconds. Caption the line verbatim. |
| 0:20–0:26 | Poke **Free Play** on the wrist menu, then play a short phrase. Show chord recognition if it is legible in the take. An on-screen “Free Play” label can replace the menu shot. | **VO 3:** “Or switch to Free Play and practice at your own pace.” Bring the piano up as the line ends. |
| 0:26–0:30 | End card on deep navy with the **cyan chamfer** mark and the title **Piano Pal**. Subtitle **AR piano practice on XREAL Aura**. | Let the last piano note ring out. |

Record each VO line as a separate take. For the coach shot, capture a guided run with a real hesitation or missed note so the feedback has a specific reason to exist. The falling tiles are an opening edit effect. Keep them out of the gameplay shots so viewers can tell which cues came from the app. Use the name **Piano Pal** with the space to match the attached Devpost draft.

## End-card logo

Use [cyan-chamfer.svg](../AR%20Piano%20Teacher%20Logo%20Exports/cyan-chamfer.svg), with [cyan-chamfer.png](../AR%20Piano%20Teacher%20Logo%20Exports/cyan-chamfer.png) as the raster fallback. The chamfered key base gives the cyan note shapes a clear piano silhouette, and the light keys read against navy. Keep the team export proportions and place the product name as separate, readable text beneath it.

## Capturing the Aura

Use the installed build of `Main_2`, the real Yamaha MIDI connection and the marker calibration. Turn **Fingering** on in the wrist menu before the guided take. Capture each beat with a few seconds of extra footage on either end.

1. Put the PC and puck on the same private Wi-Fi. If wireless ADB is already connected, start with `adb devices -l`.
2. For a fresh connection, keep the glasses on the puck and connect the PC to its charging port with a USB data cable. Run `adb devices -l`, accept the debugging prompt if needed, run `adb shell ip -4 addr show wlan0`, then run `adb tcpip 5555`. Use `adb connect PUCK_IP:5555` with the address after `inet`. Disconnect the PC cable, reconnect MIDI if needed, and check the Wi-Fi entry still says `device`. Reconnect after unplugging if it drops.
3. Run each command separately in PowerShell. The examples use the team's last working address, so replace it if the IP changed.

### scrcpy options

**Default flat preview and direct-to-MP4 capture.** Display `0` worked wirelessly on our puck and produced a 1920 × 1200 source. The camera composite showed alignment and hand-occlusion differences from what the wearer saw.

```powershell
scrcpy -s 10.178.85.32:5555 --display-id=0 --no-control --max-size=1920 --max-fps=30 --video-bit-rate=16M --audio-codec=aac --record="$env:USERPROFILE\Videos\pianopal-lesson.mp4"
```

Press **Ctrl+C** to finalize. Change the filename for each take. Use the same command with these variations as needed.

| Need | Change |
| --- | --- |
| Check the view without recording | Remove `--record=…`. |
| Try a different display | Run `scrcpy -s 10.178.85.32:5555 --list-displays`. Substitute a listed logical ID for `0`. |
| Record without a PC preview | Add `--no-playback` and keep `--record=…`. |
| Capture video only | Replace `--audio-codec=aac` with `--no-audio`. This matches the silent 1920/30 settings found in the earlier terminal history. Record room audio separately. |
| Smoother hands | Test `--max-fps=60 --video-bit-rate=24M`. Check the file for smooth motion before committing to it. |
| Wi-Fi or encoder struggles | Start with `--max-fps=30 --video-bit-rate=8M`. If needed, try `--max-size=1600`. |
| Wired fallback | Substitute the USB serial from `adb devices -l` for the IP. Check that the piano can still send MIDI with the PC cable connected. |

### Other capture paths

- **Physical display via ADB.** The team successfully recorded a 9.4-second, 1920 × 1200 test. It still didn’t reproduce the full optical occlusion. This records video on the puck and copies it after capture. Keep the app visible and the glasses awake if you get `INVALID_LAYER_STACK`. The long physical ID is for Android's `screenrecord`, while scrcpy uses the logical IDs it lists.

  ```powershell
  adb -s 10.178.85.32:5555 shell screenrecord --display-id 4626964206039778834 --bit-rate 16000000 --time-limit 15 /sdcard/pianopal-pov.mp4
  adb -s 10.178.85.32:5555 pull /sdcard/pianopal-pov.mp4 "$env:USERPROFILE\Videos\pianopal-pov.mp4"
  ```

- **Android XR Engine Hub.** In **Device Manager**, scroll past Device Logs and look for **Device Actions → Take screen recording**. If the installed Hub exposes it, compare a short take before filming the whole run. Google documents the capture button but gives no guarantee of a different Aura viewpoint. [Google's Device Manager guide](https://developer.android.com/develop/xr/engine-hub/manage-devices#configure-device-settings)
- **Phone footage.** Shoot the opening and the over-the-shoulder take vertically. If the digital capture makes the overlays look misplaced, try a short through-one-lens insert with the phone camera where the pupil would be and the glasses still powered and tracking. If that setup is too fiddly, use the phone for the physical key press and the clearest digital clip for the app cues and sheet. XREAL describes Aura as optical see-through, so the direct view and recorded composite can differ. [XREAL Aura display guidance](https://tutorials.xreal.com/docs/aura/developers/app-design-guidelines/)

Record the real key sound with a phone or room mic. Check the scrcpy file for coach audio. If we record audio separately, use one clear key press or clap to sync it.

## Remotion and Brag skills

- **Remotion.** If assembling with an agent, invoke the installed `remotion-best-practices` skill. Set the composition to **1080 × 1920, 30 fps, 900 frames**. Preview with `npx remotion studio`. Use the tile animation on the opening transition, then reframe each landscape Aura clip around the detail that matters. Give the sheet and keyboard separate crops if both are too small in one frame. Once the team approves the preview, use `npx remotion render` and export an H.264 MP4 with AAC audio.
- **Brag.** If a teammate has the Brag skill, use it to draft the end card or Devpost feature bullets from the finished footage. The claims we can substantiate on camera are physical-key guidance on XREAL Aura, a floating score, spoken feedback after a phrase, and switching to Free Play. The Brag skill wasn’t found in the local skill directories checked for this handover.

## Before submitting

- Watch the whole 30 seconds on a phone with sound. Check the piano cue and press are legible in portrait, the spoken feedback is audible, and the title stays on screen long enough to read.
- Keep a clean vertical export and the original POV, room audio and phone takes in the team's shared drop. Use shot names such as `01-aura-on`, `02-guided-lesson`, `03-coach-feedback`, and `04-free-play`.
- Check that Devpost has the intended video and public repo. Keep any APK built with real API keys out of the submission and shared drop.
