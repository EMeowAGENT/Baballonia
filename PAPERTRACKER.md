# PaperTracker (native UDP) on Linux

This fork can receive the PaperTracker Android app directly over UDP. No
v4l2loopback device and no `papertrackerd` process is needed for this path.

## Direct UDP mode

1. Make sure nothing else is listening on UDP **45454** (in particular
   `papertrackerd` from `papertracker-linux`). Both receivers bind the same
   port, so only one can run at a time.
2. Allow UDP 45454 in the firewall.
3. In the headset app, set the PC address to the Linux machine's LAN IP and
   start the bridge.
4. In Baballonia, pick:
   - `PaperTracker Face (UDP)` for the face camera
   - `PaperTracker Left Eye (UDP)` / `PaperTracker Right Eye (UDP)` for the eyes

The module ACKs `PTRACKER_PROBE_V1` with `PTRACKER_ACK_V1` (15 bytes) and
answers from the address the probe arrived on using `IP_PKTINFO`, so
multi-homed hosts work as long as the probe reaches the expected interface.

Custom address form (for a specific NIC or port):

```text
paperudp://192.168.1.10:45454/face
paperudp://192.168.1.10:45454/left
paperudp://192.168.1.10:45454/right
```

If the camera is started before the headset bridge, Baballonia waits up to
30 seconds for the first frame instead of the usual 13 seconds.

## v4l2loopback fallback

The existing `papertracker-linux` daemon can still be used:

- stop the direct UDP cameras in Baballonia first (they release port 45454)
- run `papertrackerd` and use `/dev/video10`, `/dev/video11`, `/dev/video12`
  with the **V4L2 Camera** backend

Both paths are intentionally kept; they are mutually exclusive only while
they are both trying to bind UDP 45454.
