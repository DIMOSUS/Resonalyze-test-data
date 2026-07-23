# Resonalyze test data

Real acoustic measurements backing [Resonalyze](https://github.com/DIMOSUS/Resonalyze)'s
regression tests, consumed by the main repository as the `assets/test_data`
submodule.

A 4-way stereo system measured with loopback-referenced exponential sweeps
(44.1 kHz, 4 averages): transfer impulse responses of the subwoofer
(`sub woof.json`, `sub woof closed window.json`), woofers, mids and tweeters
of both channels, in Resonalyze's native impulse-response JSON format
(`transferRealSamples` + `transferCoherence`), plus `left_channel.json` —
the Virtual DSP crossover project that pairs the left-channel drivers
(sub LP 80 / woof BP 80-175 / mid BP 175-1300 / twr HP 1800, BW24).

The woofer/mid pair pins the first-arrival pre-ringing regression: in the
shared 87.5-350 Hz band the woofer's direct sound arrives at 11.466 ms,
~8 dB below its reverberant reflection cluster.

Files open directly in Resonalyze (Impulse Response mode and the Virtual DSP
panel).

## v3: the auto-delay defense series dataset

`v3/` holds the second measured cabin (4-way stereo, 44.1 kHz, 8 averages)
behind the auto-delay defense series
([Resonalyze PR #52](https://github.com/DIMOSUS/Resonalyze/pull/52)): a
90–180 Hz cabin mode overpowering the direct front under steep low-frequency
crossover slopes. Alongside the transfer IRs of all seven drivers:

- `virtual-dsp-session_bad.json` — the modal-latch seed failure (mids and
  tweeters parked a full period late, mids inverted);
- `virtual-dsp-session_bad2.json` — the self-verified cross-side link
  failure (a fabricated −8.4 ms L/R split scene-locked the right midbass
  ~11 ms ahead of everything);
- `session_validated.json` — the owner-ear-validated reference tune the
  fixed engine reproduces at any sub gain: sub inverted at 0 ms, clean
  B/C/D stack, C–D paired, every channel's IR onset on one line.

`harness/RealDataAlignmentHarness.cs` is the validation harness used
throughout the series: drop it into `tests/Resonalyze.App.Tests/` of the
main repository and set `RESONALYZE_FIELD_DATA_V3` / `..._V2` to your
checkout (the session files reference records via absolute `sourceFilePath`
entries — either mirror that layout or edit the paths). It replays all
sessions headlessly, sweeps a 16-config midbass/mid crossover matrix,
probes gain sensitivity and junction sub-band landscapes; every test
returns silently when the data is absent, so the file is CI-safe.
