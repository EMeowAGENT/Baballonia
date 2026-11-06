# Eyelid openness (PFLD) enhancer

The stock eye model only had blink data with both eyes labelled together, so it
could not reliably separate left/right eyelid movement. Instead of collecting
unilateral wink frames and retraining the eye model, this fork runs a dedicated
landmark model (`pfld-sim.onnx`) on each eye at runtime and replaces the eye
model's lid channels with values derived from the eyelid landmarks.

The primary eye model still handles gaze and the normal open/closed blink
model. The enhancer only overrides the two lid channels before the OneEuro
filter and the expression mapping.

## How it works

For every frame:

1. `DualImageTransformer` produces a two-channel image:
   - channel 0 = left camera
   - channel 1 = right camera
2. `PfldSimEyelidEnhancer` splits the image and runs `pfld-sim.onnx` on each
   eye (112x112 grayscale input).
3. The first six landmarks give the eye aspect ratio (EAR):
   - width = distance between the eye corners
   - height = average of the upper/lower lid distances
   - EAR = height / width
4. Each eye has its own open/closed percentile bounds. EAR is mapped to a
   closedness value in `[0, 1]` (1 = closed, 0 = open), matching the eye
   model's original lid convention.
5. The values are written back with the same left/right swap used by
   `EyeProcessingPipeline.ProcessExpressions`, so the OSC/VRCFT output is
   physical left/right.

Because each eye is measured independently, unilateral winks work without
collecting wink training data.

## Calibration

`pfld-sim.onnx` measures relative openness, so each camera/eye needs its own
open and closed baseline. The Calibration page has a **Calibrate Eyelids**
button:

1. Click the button.
2. For three seconds, look at the screen and blink naturally a few times.
   Do not wink or squeeze only one eye.
3. The enhancer records the per-eye EAR samples and stores:
   - closed = 2nd percentile
   - open = 90th percentile, expanded by `1 / 0.9` to leave headroom for
     wide-open eyes
4. The bounds are persisted in `LocalSettings.json` as
   `EyelidCalibrationState`.

If no manual calibration has been saved, the enhancer falls back to a rolling
percentile window over the last 400 samples; with fewer than 20 samples it uses
the defaults `open = 0.4`, `closed = 0.1`.

## Settings

- **Eyelid enhancer**: `PFLD` (default) or `Disabled`.
  - Disabling it returns to the primary eye model's lid output.
- **Eyelid mode**:
  - `Both eyes` (default): run and use both eyes independently.
  - `Left eye only`: use the left eye for both outputs.
  - `Right eye only`: use the right eye for both outputs.

The single-eye modes are useful when one camera is occluded or when you want to
reduce the extra inference cost.

The model path can be overridden with `EyeHome_EyelidModel` (default
`pfld-sim.onnx`). The file is shipped next to the application and is also
embedded as a resource.

## Calibration routine changes

The `wink_left` / `wink_right` capture steps were removed from the full, quick
and blink-only calibration routines. Those routines now only capture gaze and a
normal both-eyes-closed blink. The trainer still trains the blink model from
`blink.bin` and `gaze.bin` as a fallback when the enhancer is disabled.

The old `wink_left.bin` / `wink_right.bin` files in
`~/.config/ProjectBabble/ModelData/` are no longer merged or used; they can be
deleted. Existing models trained with wink data can stay, because the enhancer
overrides their lid output at runtime.

## Model and license

`pfld-sim.onnx` comes from the m-RNA/Baballonia `AddEyelidOpenness` branch,
which in turn credits the EyeTrackVR PFLD model. It is distributed under the
same Babble Software Distribution License 1.0 used by the rest of the project.
