# Video decode test fixtures

All paths are relative to this file (`Fixtures/video/`).

| File | Bytes | What it is | Source | Licence |
|---|---:|---|---|---|
| `gate.mpd` | 1,947 | DASH manifest, 5×4 s segments, 20 s total, 640x360@30fps | generated locally with ffmpeg (command below) | none (synthetic test pattern, generated for this repo) |
| `init-stream0.m4s` | 835 | DASH video init segment (H.264) | generated | none |
| `init-stream1.m4s` | 765 | DASH audio init segment (AAC) | generated | none |
| `chunk-stream0-00001.m4s` | 153,175 | video media segment 1 (0-4s) | generated | none |
| `chunk-stream0-00002.m4s` | 157,490 | video media segment 2 (4-8s) | generated | none |
| `chunk-stream0-00003.m4s` | 145,810 | video media segment 3 (8-12s) | generated | none |
| `chunk-stream0-00004.m4s` | 151,845 | video media segment 4 (12-16s) | generated | none |
| `chunk-stream0-00005.m4s` | 157,687 | video media segment 5 (16-20s) | generated | none |
| `chunk-stream1-00001.m4s` | 32,913 | audio media segment 1 | generated | none |
| `chunk-stream1-00002.m4s` | 32,908 | audio media segment 2 | generated | none |
| `chunk-stream1-00003.m4s` | 32,877 | audio media segment 3 | generated | none |
| `chunk-stream1-00004.m4s` | 33,014 | audio media segment 4 | generated | none |
| `chunk-stream1-00005.m4s` | 33,125 | audio media segment 5 | generated | none |
| `chunk-stream1-00006.m4s` | 476 | audio media segment 6 (tail remainder) | generated | none |
| `bear-1280x720-av_frag.mp4` | 764,465 | clear fragmented MP4 (H.264 + AAC, 1280x720) | Chromium `media/test/data/` | BSD-3-Clause (see `CHROMIUM-LICENSE.txt`) |
| `bear-1280x720-a_frag-cenc.mp4` | 74,428 | fragmented MP4, audio track CENC-encrypted, carries a subsample map | Chromium `media/test/data/` | BSD-3-Clause (see `CHROMIUM-LICENSE.txt`) |
| `CHROMIUM-LICENSE.txt` | 1,536 | Chromium's top-level `LICENSE`, covering the two `bear-*` files above | Chromium (`chromium/src/LICENSE`) | BSD-3-Clause |

**Total: 1,775,296 bytes (~1.69 MiB) across 17 files.**

## `gate.mpd` — the seek-gate clip

Generated with a known, fixed 2-second GOP (60 frames @ 30 fps) so the seek-gate test has a documented
GOP boundary to seek against — none of the downloaded/copied sample files document their GOP structure.
Bitrate was capped (`-b:v 300k -b:a 64k`) to keep the fixture small; the uncapped encode came out to
~2.19 MB total, over the ~2 MB budget for a checked-in fixture.

```
ffmpeg -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 20 \
       -c:v libx264 -b:v 300k -g 60 -keyint_min 60 -sc_threshold 0 -c:a aac -b:a 64k \
       -f dash -seg_duration 4 gate.mpd
```

ffmpeg version used: `8.1.2-full_build-www.gyan.dev` (found on PATH at
`C:\Users\ChristosKarapasias\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe`).

## Chromium bear fixtures

Copied as-is (no re-encoding) from a local checkout at `C:\WAVEE\chromium-media\media\test\data\`, no
network access performed. `bear-1280x720-av_frag.mp4` is a clear fragmented MP4; `bear-1280x720-a_frag-cenc.mp4`
carries CENC-encrypted audio with a subsample (`saiz`/`saio`) map. Both are covered by Chromium's
BSD-3-Clause licence, copied here verbatim as `CHROMIUM-LICENSE.txt`.
