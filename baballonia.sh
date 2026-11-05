#!/bin/sh
# The application resolves some assets (Firmware/, Calibration/) relative to
# the working directory, so always start it from the install prefix.
cd /opt/baballonia-git || exit 1
exec ./Baballonia.Desktop "$@"
