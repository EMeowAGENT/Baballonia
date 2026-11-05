# Unilateral blink (wink) calibration

The stock `BasicCalibration` only recorded **both eyes closed** and **both eyes
open**. The blink models therefore could not learn left/right independently.

This fork adds two capture steps after the normal blink step:

| Step | Label | What to do |
| --- | --- | --- |
| `wink_left.bin` | `RoutineLeftLid=0`, `RoutineRightLid=1` | close **only the left eye**, keep the right eye open |
| `wink_right.bin` | `RoutineLeftLid=1`, `RoutineRightLid=0` | close **only the right eye**, keep the left eye open |

The stock calibration overlay does not have a wink animation, so these steps
reuse its `blink` routine for the timer/visual prompt. **When the overlay shows
the blink prompt for a wink step, only close the requested eye.**

The two bins are merged into `user_cal.bin` together with `gaze.bin` and
`blink.bin`.

## Trainer

The package ships a patched copy of the trainer scripts under
`/opt/baballonia-git/Calibration/Linux/Trainer/runtime/`. Its
`select_squint_wide_blink()` keeps the left and right lid labels independent
instead of copying the left lid to both eyes.

`babble_data` and the Python dependencies still come from the system
`babbletrainer` package.

After re-running calibration, train again. Existing models trained before the
wink data was added cannot distinguish unilateral blinks and should be
retrained.
