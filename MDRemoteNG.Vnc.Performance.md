# Built-in VNC latency work

The latency baseline is the shipped MDRemoteNG 1.78.5 installer, built from
`cbdb944` plus the current installer and local-network features. That version
already includes bulk pixel decoding, compressed rectangles and dirty-region
batching. Earlier uncompressed builds are not the acceptance baseline.

The updated helper uses a persistent .NET `ZLibStream` compressor for standard
RFB encoding 6. It reuses bounded output storage and keeps compression history
across rectangles. The viewer uses a bounded native inflater with exact pixel
counts, guarded compressed tails and failed-stream poisoning. Raw and Hextile
fallbacks remain available; the viewer can still connect to older helpers.

The helper targets up to 60 captures per second. A private one-shot timer wakes
the framebuffer worker at its deadline without holding the protocol request
lock or changing Windows' global timer resolution. Early callbacks reuse the
previous capture and skip repeated dirty scanning. Requests coalesce, missed
deadlines do not create catch-up work, and expensive capture/scan work lowers
the target to a 400 ms per second work budget. Unconnected helpers do not capture
pixels; authenticated viewers without pending requests do not poll the desktop.

## Measurement

`MDRemoteNG.Vnc.Benchmarks` exercises the production host, TLS tunnel, decoder
and Windows Forms viewer with injected synthetic pixels. It includes 1080p
and 3840×2160 workloads, seeded idle patches, full-frame changes, and a 30 Hz
continuous producer. The exact compiled harness must be identical on both
sides of a comparison. Every runtime assembly hash and raw sample is retained.

Timing starts before copying precomputed pixels into the synthetic source and
ends after the actual viewer's `OnPaint` completes into preallocated offscreen
graphics. The paint clip matches the production dirty region. Continuous
visible-frame age is integrated over elapsed time and split at each paint;
discarded source frames cannot count as displayed frames. These measurements
exclude monitor scan-out and real Windows screen capture. The real CONDOR
refresh probe complements them by exercising its actual desktop capture and
local network.

The fixed acceptance threshold is at least 70% reduction in all six synthetic
metrics: idle-patch p50/p95, full-frame p50/p95, and continuous time-weighted
visible-frame-age p50/p95. Runs use production capture settings. Loopback,
1 Gbit/s with 2 ms one-way latency, and a constrained 100 Mbit/s with 8 ms
one-way latency are reported separately. Serialization and network round trips
impose lower bounds, so a passing profile does not imply a universal result.

The real 1.78.5 CONDOR baseline, captured on its unlocked 3840×2160 desktop,
uses explicit refresh requests, pinned TLS, bitmap decoding and offscreen paint:

| Refresh | Samples | p50 | p95 | Required new p50 | Required new p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 64×64 | 40 | 134.93 ms | 150.69 ms | ≤40.48 ms | ≤45.21 ms |
| Full frame | 20 | 864.54 ms | 884.91 ms | ≤259.36 ms | ≤265.47 ms |

Baseline report SHA-256:
`5C78010C40E8250F6226E00E1A667C87895A39F3D625DCBF7A3B7BF67E881B4B`.
Explicit refresh latency is distinct from spontaneous presentation latency;
both are needed to assess the change. No probe sends Windows input or accesses
the clipboard, and real desktop pixels are never written into a report.

## Controlled 4K results

Both primary synthetic profiles pass all six 70% gates with the helper and
viewer updated. These are fresh paired runs of the same compiled harness,
SHA-256 `63F3B65713F1D04D5762252DDEB2D2189943C0B7F46E49C84DBB534F89FBDD58`.
Each run uses 30 idle patches, 20 full frames and 10 seconds of continuous
production at 30 Hz. Values below are p50 / p95 in milliseconds.

| Profile | Metric | Shipped 1.78.5 | Updated | Reduction |
| --- | --- | ---: | ---: | ---: |
| Loopback | Idle patch | 103.29 / 168.64 | 18.59 / 34.12 | 82.00% / 79.76% |
| Loopback | Full frame | 446.85 / 575.93 | 95.34 / 109.20 | 78.66% / 81.04% |
| Loopback | Visible-frame age | 600.05 / 812.36 | 146.34 / 192.60 | 75.61% / 76.29% |
| Simulated LAN | Idle patch | 119.59 / 182.09 | 23.14 / 39.37 | 80.65% / 78.38% |
| Simulated LAN | Full frame | 440.42 / 605.29 | 105.60 / 122.12 | 76.02% / 79.83% |
| Simulated LAN | Visible-frame age | 624.10 / 823.30 | 150.76 / 200.05 | 75.84% / 75.70% |

The software network model uses bounded encrypted-byte queues and private
high-resolution async timers. Separate timer characterization measured about
0.3–0.6 ms of median scheduling overhead at the configured short waits. The
earlier coarse-timer model is excluded from these final comparisons; all final
baselines and updated runs use the corrected identical harness. This remains
a software simulation and does not replace the real CONDOR acceptance probe.

The selected release is one complete tested implementation, with all profile
outcomes retained in `MDRemoteNG.Vnc.Benchmarks/RESULTS-1.78.5.json`. The 1080p
loopback profile also passes all six gates. The 1080p LAN profile passes three;
both constrained profiles pass two, missing the full-frame and continuous-age
goals. At 4K on the constrained profile, those reductions range from 44.62% to
53.89%. Higher native compression levels saved only 17.5% of fixture bytes and
added enough codec work to worsen fast-LAN latency, so the release retains the
fastest setting. These results do not support a universal 70% improvement claim.

### Installer verification

The first 1.78.6 installer helper assemblies and viewer were also measured
under its bundled .NET 10.0.1 runtime using the same frozen harness. This
packaged variant does **not** pass the fixed six-metric target: simulated LAN
passes zero gates and loopback passes one. These results are retained rather
than substituting the faster development-runtime results above.

| Profile | Metric | Packaged p50 / p95 | Reduction against fixed 1.78.5 baseline |
| --- | --- | ---: | ---: |
| Loopback | Idle patch | 72.90 / 119.07 ms | 29.43% / 29.39% |
| Loopback | Full frame | 148.73 / 172.06 ms | 66.72% / 70.12% |
| Loopback | Visible-frame age | 201.10 / 263.22 ms | 66.49% / 67.60% |
| Simulated LAN | Idle patch | 63.09 / 156.50 ms | 47.25% / 14.05% |
| Simulated LAN | Full frame | 155.00 / 196.96 ms | 64.81% / 67.46% |
| Simulated LAN | Visible-frame age | 212.73 / 280.95 ms | 65.91% / 65.88% |

PE metadata subsequently identified a packaging defect: the custom
`Release Self-Contained` configuration disabled compiler optimizations in
referenced projects. The passing development assemblies had optimizations
enabled. A root build property now enables optimizations for this release
configuration across projects. The first installer is superseded; its failed
results remain recorded. The corrected installer must be measured separately.

The corrected installer enables optimizations in all 11 inspected application
and helper assemblies. Its exact helper/viewer files were measured under the
bundled runtime without changing the harness or fixed baselines:

| Profile | Metric | Optimized package p50 / p95 | Reduction |
| --- | --- | ---: | ---: |
| Loopback | Idle patch | 28.24 / 59.94 ms | 72.66% / 64.46% |
| Loopback | Full frame | 114.54 / 184.57 ms | 74.37% / 67.95% |
| Loopback | Visible-frame age | 146.48 / 194.95 ms | 75.59% / 76.00% |
| Simulated LAN | Idle patch | 29.84 / 60.20 ms | 75.05% / 66.94% |
| Simulated LAN | Full frame | 119.73 / 157.38 ms | 72.81% / 74.00% |
| Simulated LAN | Visible-frame age | 160.31 / 215.51 ms | 74.31% / 73.82% |

The optimized package passes five of six LAN gates and four of six loopback
gates. Its patch p95 in both profiles and loopback full-frame p95 still miss
the threshold. The 70% goal remains unmet. Final acceptance also requires
installing the updated host and viewer and repeating the live probe against
the fixed real CONDOR baseline above.

### Row-band follow-up

A subsequent host-cache change skips contiguous unchanged row bands after
whole-frame knowledge has been established and remembers only the final changed
rectangles. Unknown pixels, cropped requests, resize handling and the 64-rectangle
bound retain their previous semantics. This removes hundreds of thousands of
short row comparisons and a full 32 MiB cache copy from a small 4K update.
Capture-rate and work-budget settings are unchanged.

The candidate replaces only the helper Desktop assembly in the exact optimized
1.78.6 runtime fixture; its actual precompiled viewer and all other DLLs remain
identical. Both fixed primary profiles now pass all six gates:

| Profile | Metric | Row-band candidate p50 / p95 | Reduction |
| --- | --- | ---: | ---: |
| Loopback | Idle patch | 21.17 / 34.89 ms | 79.50% / 79.31% |
| Loopback | Full frame | 103.88 / 129.07 ms | 76.75% / 77.59% |
| Loopback | Visible-frame age | 147.39 / 193.36 ms | 75.44% / 76.20% |
| Simulated LAN | Idle patch | 23.60 / 46.01 ms | 80.26% / 74.73% |
| Simulated LAN | Full frame | 116.72 / 162.62 ms | 73.50% / 73.13% |
| Simulated LAN | Visible-frame age | 153.95 / 207.48 ms | 75.33% / 74.80% |

Seventy-five focused host checks and 102 viewer checks in default and strict
compression modes pass for this candidate. Five added cache checks include
reconstruction of 200 seeded updates across cropped and resized displays.
The 1.78.7 installer has now been built and measured using its actual helper
and precompiled viewer under its bundled runtime. Both profiles pass all six
fixed gates, with every earlier result retained:

| Profile | Metric | Actual 1.78.7 p50 / p95 | Reduction |
| --- | --- | ---: | ---: |
| Loopback | Idle patch | 19.25 / 43.62 ms | 81.37% / 74.13% |
| Loopback | Full frame | 111.88 / 157.06 ms | 74.96% / 72.73% |
| Loopback | Visible-frame age | 148.68 / 198.16 ms | 75.22% / 75.61% |
| Simulated LAN | Idle patch | 21.05 / 41.34 ms | 82.40% / 77.30% |
| Simulated LAN | Full frame | 116.51 / 151.55 ms | 73.55% / 74.96% |
| Simulated LAN | Visible-frame age | 160.40 / 222.34 ms | 74.30% / 72.99% |

MSI SHA-256:
`9AEA1F741A7112D5F88476A465828E5B9FBC4BF2C52F60C6AA2FB4E012BEA041`.
All 872 extracted files match the publish output. The exact helper passes
88 host checks (75 focused checks plus 13 existing feature checks), and both
desktop/helper codec variants pass 102 checks in default and strict modes.
Eight startup, 18 connection-file and 13 MSI metadata checks also pass.
The real CONDOR measurement and interactive confirmation remain required;
the goal is not yet complete. Other network/workload profiles have not been
remeasured for 1.78.7, so their earlier misses cannot be reported as resolved.

The intended source passes 75 helper/TLS/cache checks and 102 actual viewer,
codec and pixel-transfer checks, with the latter repeated under strict zlib
validation. Nine benchmark self-checks cover exact visible-age integration
and timer cancellation/teardown. Synthetic pixel tests include cropped and
resized caches, fragmented reads, alternate formats, signed/padded bitmap
strides, compression history, malformed data, output bounds and disconnects.

See `MDRemoteNG.Vnc.Benchmarks/README.txt` for reproduction and comparison
commands. `External/RemoteViewing/PROVENANCE.md` and
`External/VncSharpCore/PROVENANCE.md` identify the vendored source and changes.
