# Unilateral blink (wink) calibration

The stock `BasicCalibration` only recorded **both eyes closed** and **both eyes
open**. The blink models therefore could not learn left/right independently.

This fork adds two capture steps after the normal blink step:

| Step | Label | What to do |
| --- | --- | --- |
| `wink_left.bin` | `RoutineLeftLid=1`, `RoutineRightLid=0` | close **only your left eye**, keep your right eye open |
| `wink_right.bin` | `RoutineLeftLid=0`, `RoutineRightLid=1` | close **only your right eye**, keep your left eye open |

`0` means closed and `1` means open. The labels look "swapped" because
`FrameCollector` stores the right camera in `LeftJpeg` and the trainer pairs
`RoutineLeftLid` with `LeftJpeg`; they describe the stored camera channels, not
the physical eyes.

The stock calibration overlay does not have a wink animation, so these steps
reuse its `blink` routine for the timer/visual prompt. **When the overlay shows
the blink prompt for a wink step, only close the requested eye.** The overlay
does not tell you which eye it wants, so follow the step order:

1. Normal blink prompt: close both eyes and hold them closed for the capture.
2. First extra blink prompt: close only your left eye (`wink_left`) and hold it
   closed; keep your right eye open and relaxed.
3. Second extra blink prompt: close only your right eye (`wink_right`) and hold
   it closed; keep your left eye open and relaxed.

Use your own left/right, not the side of the preview: the eye cameras can be
horizontally flipped, so the preview is not a reliable left/right reference.

The two bins are merged into `user_cal.bin` together with `gaze.bin` and
`blink.bin`, using whatever bins are already present in `ModelData`. `BlinkOnly`
keeps the previous `gaze.bin`; `GazeOnly` merges only `gaze.bin` + `blink.bin`,
so it drops the wink bins and trains a model without unilateral blink support.
If a merged file is missing, the merge fails with `FileNotFoundException`.

## Trainer

The package ships a patched copy of the trainer scripts under
`/opt/baballonia-git/Calibration/Linux/Trainer/runtime/`. Its
`select_squint_wide_blink()` keeps the left and right lid labels independent
instead of copying the left lid to both eyes.

`babble_data` and the Python dependencies still come from the system
`babbletrainer` package.

After re-running calibration, train again. Existing models trained before the
wink data was added cannot distinguish unilateral blinks and should be
retrained. The wink bins captured before the label fix are inverted, so they
must be overwritten by a new calibration; do not merge them into a new model.
