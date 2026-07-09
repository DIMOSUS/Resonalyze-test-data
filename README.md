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
