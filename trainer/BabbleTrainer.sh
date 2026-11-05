#!/bin/sh
# Run the patched trainer against the system Python/babble_data module.
# Keep the caller's working directory: Baballonia points it at
# ~/.config/ProjectBabble/Trainer, which is where checkpoints are written.
DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec python "$DIR/runtime/main.py" "$@"
