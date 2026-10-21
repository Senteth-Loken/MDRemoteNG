Reproducible synthetic VNC latency benchmark (Windows/.NET 10)

This console project uses the real VncSharpCore control, VncSharingHost,
VncClientTunnel and pinned TLS on loopback. It never shows a window or calls
real capture, keyboard, pointer, clipboard, host-configuration or global-hook APIs.
All desktop callbacks are injected fake callbacks, and zero input is asserted.

Build Release, x64, then run its DLL with dotnet:
  --self-test
  --label shipped-1.78.5 --profile loopback --output baseline.json
  --label candidate --profile loopback --output candidate.json
  --compare baseline.json candidate.json --output comparison.json

For the actual 3840x2160 CONDOR display, add --workload four-k to BOTH runs.
This separately declared workload uses 30 idle patches, 20 full frames and a
10-second continuous producer. Its 17 precomputed templates require about
564 MB, plus framebuffer/paint/codec memory. Standard 1080p remains 60/40/15s.

Run the SAME compiled harness in separate copied runtime folders. Overlay the
exact baseline or candidate MDRemoteNG.Core.dll, MDRemoteNG.Desktop.dll,
RemoteViewing.dll and VncSharpCore.dll there. RemoteViewing.Windows.Forms.dll
is an optional legacy adapter when copying the whole shipped 1.78.5 bundle;
the candidate's vendored server core does not emit or use that forms adapter.
Do not replace files inside a live installation. Runtime hashes are recorded.
The primary baseline for the new latency goal is the shipped 1.78.5 installer;
the cursor-fixed 6fbfb2a version is historical context, not the acceptance baseline.

Full runs include 60 seeded, randomly spaced idle 64x64 updates; 40 full-frame
updates; and a 15-second, 30 Hz continuous producer. All 17 nonuniform frame
templates are generated before timing. Timing starts before taking the frame
lock and copying its precomputed pixels; it ends after the actual hidden
control's OnPaint has completed on a preallocated Graphics and painted markers/
content samples match. The Graphics clip matches production Invalidate(region):
64x64 for patches and the full region for full/continuous frames. Save/Restore
preserves the clip between paints. A full DrawToBitmap snapshot is measured
only during warmup as an out-of-band diagnostic, never in the latency samples.
This is an offscreen paint boundary, not a measurement of monitor scan-out.
Presentation/lock time, decode readiness and paint time are separately recorded.

A nominal 10 ms heartbeat samples the source timestamp of the frame still
painted, the latest presented ID, ID lag and UI queue delay. Actual elapsed-time
weights determine continuous visible-frame-age quantiles. Segments split at
every paint transition; start/end ages and durations are stored, and quantiles
integrate their exact linear age ramps rather than weighting one endpoint.
Delivered-frame age,
skipped producer IDs, skipped scheduling deadlines, frame intervals and final
drain time remain visible; skipped frames cannot masquerade as more display FPS.
Frame-sample quantiles use nearest rank; visible-age quantiles use the exact
integrated linear-age distribution described above. Every raw sample is
retained. A failed/incomplete run
cannot pass comparison. --quick is a smoke test only, not acceptance evidence.

Profiles are fixed before measurement:
  loopback     no imposed delay/bandwidth cap (bounded proxy still present)
  lan          1 Gbit/s, 2 ms one-way delay
  constrained  100 Mbit/s, 8 ms one-way delay
The proxy forwards encrypted bytes in bounded FIFO queues, with serialization
deadlines rather than adding latency per TCP read. It models no packet loss or
jitter. Delivery granularity is up to 64 KiB (up to 5.2ms at 100 Mbit/s), and
at most 32 queued chunks plus one pending writer per direction hold memory.
Network caps impose physical lower bounds; report every selected profile.
CPU/allocation totals include the in-process synthetic host, viewer and proxy.
Each outstanding wait uses a private Windows high-resolution one-shot waitable
timer and a registered thread-pool callback, with cancellation and owned safe
handles. There is no global timer-resolution change or busy spin. Unsupported
systems use Task.Delay; Network.Model records the mechanism and any fallback.
This corrects the v1 software-model defect where coarse Task.Delay could turn a
2.5 ms deadline into roughly 15.6 ms. Both versions must be rerun with this same
new harness; the preserved v1 failures are separate diagnostic artifacts. Small
standalone timer calibration is model validation, not latency-goal evidence.

Comparison reports six independent >=70% reduction verdicts: idle-patch p50/p95,
full-frame present-to-paint p50/p95, and time-weighted continuous visible age
p50/p95. Arrival age and frame intervals are diagnostics, not replacements for
these metrics. Production acceptance also requires no --server-rate override.
--server-rate 8|30|60 uses reflection on the synthetic session for pacing
experiments only and overrides the library scheduler, not a separate capture
policy in a newer helper. LibraryMaxUpdateRate and PolicyTargetCaptureRate are
reported separately. It is never a hidden change to production settings.
Compare only matching workload/seed/profile runs with identical harness hashes.

Selected V2 comparison against the exact shipped 1.78.5 runtime
-------------------------------------------------------------
RESULTS-1.78.5.json contains all six comparisons, per-run summaries, runtime
hashes and raw-report hashes. The primary 4K scope was declared before these
runs. All six gates pass for 4K loopback and 4K LAN, with reductions ranging
from 75.61% to 82.00%. The complete profile outcomes are:
  4K    loopback       6/6 gates pass
  4K    LAN            6/6 gates pass
  4K    constrained    2/6 gates pass
  1080p loopback       6/6 gates pass
  1080p LAN            3/6 gates pass
  1080p constrained    2/6 gates pass
The 1080p LAN full-frame p95 and continuous visible-age gates miss 70%; both
constrained profiles miss full-frame and continuous visible-age gates. These
results support the stated primary synthetic scope, not a universal 70% claim.
All 12 raw reports, preserved v1 failures, timing microprobes and source/runtime
manifests are retained under .build/vnc-latency/. No failed outcome was replaced
by a selective rerun. Further variants must retain the same six gates/workloads
and label their candidate identity and baseline reuse explicitly.
V2 is selected as an entire implementation and complete dataset. A subsequent
V3 event-driven library-scheduler prototype missed the primary 4K loopback
patch p95 gate and was rejected as a whole variant; its six fixed-baseline
comparisons remain in .build/vnc-latency/final-native-v3/. No V3 metric replaces
a V2 metric. Exact packaged-runtime verification and the actual CONDOR check
after installation remain separate requirements before claiming the live goal.
