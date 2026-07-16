# Manual acceptance tests

## Blu-ray HLS playback and seeking

`bluray_hls_acceptance.py` is a black-box, real-media acceptance test for
Blu-ray folder playback. It drives a live Jellyfin server and the exact `mpv`
binary built by `jellyfin-desktop`; it is not intended for CI because the test
disc is not distributable.

The default fixture is the local *Atlantis: The Lost Empire* item and playlists
`00100.MPLS` and `00150.MPLS`. For each playlist the test:

1. selects and refreshes the playlist through Jellyfin's API;
2. negotiates playback with Jellyfin Desktop's device profile;
3. starts HLS in headless mpv and requires decoded audio and video;
4. performs several deep exact seeks and requires playback to restart near the
   requested position;
5. rejects seeks that require more than twelve media segments before playback
   first recovers (cache prefetch during the separate stability check is ignored); and
6. checks local FFmpeg logs for missing-audio and fragmented-MP4 failures.

The test ends with the last requested playlist selected. With the defaults this
is `00150.MPLS`.

Run the warm-cache iteration loop:

```bash
JELLYFIN_PASSWORD='<test password>' \
  python3 tests/Manual/bluray_hls_acceptance.py --mode quick
```

Run the final gate, which first resets automatic playlist selection:

```bash
JELLYFIN_PASSWORD='<test password>' \
  python3 tests/Manual/bluray_hls_acceptance.py --mode full
```

Useful overrides include `--server`, `--item-id`, `--mpv`, `--playlists`,
`--seeks`, `--device-profile-log`, and `--server-log-dir`. Artifacts are written
under `/tmp/jellyfin-bluray-acceptance` by default. Access tokens are redacted
from persisted mpv logs. Pass `--playlists CURRENT` to exercise an item's
existing or automatically selected playlist without first enumerating and
changing the disc's playlist selection.

Use `--require-video-copy` for passthrough coverage. It checks every collected
server FFmpeg command and fails if video is encoded instead of using
`-codec:v:0 copy`. Audio selection is automatic unless
`--audio-stream-index` is supplied explicitly.
