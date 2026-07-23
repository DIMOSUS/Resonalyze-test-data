using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using Resonalyze;
using Resonalyze.Dsp;

namespace Resonalyze.App.Tests;

// Field-validation harness behind the Resonalyze auto-delay defense series
// (Resonalyze PR #52): replays the real cabin sessions headlessly, sweeps
// the 16-config midbass/mid crossover matrix and probes junction landscapes.
// Drop this file into tests/Resonalyze.App.Tests/ of the main repository and
// point the environment variables below at a checkout of Resonalyze-test-data
// (the session files reference the records by the sourceFilePath entries
// inside them — either mirror that layout or edit the paths). All tests
// return silently when the data directory is absent, so the file is CI-safe.
public sealed class RealDataAlignmentHarness
{
    private static readonly string V3Dir =
        Environment.GetEnvironmentVariable("RESONALYZE_FIELD_DATA_V3")
        ?? @"D:\hobby\AMP\v3";
    private static readonly string V2Dir =
        Environment.GetEnvironmentVariable("RESONALYZE_FIELD_DATA_V2")
        ?? @"D:\hobby\AMP\v2\head_90_grad";
    private static readonly string OutDir = Path.Combine(
        Path.GetTempPath(), "resonalyze-field-harness");

    private static readonly ConcurrentDictionary<string, (Complex[] Ir, int SampleRate)>
        RecordCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed class HarnessChannel : IAlignmentChannel
    {
        public required string Name { get; init; }
        public required int SampleRate { get; init; }
        public required VirtualCrossoverChannelSettings Settings { get; init; }
        public required Complex[] MeasuredIr { get; init; }
        public bool Mono { get; init; }
        public HarnessChannel? Peer { get; set; }
    }

    private sealed class LoadedSession
    {
        public required VirtualCrossoverProjectFile Project { get; init; }
        public required List<HarnessChannel> Left { get; init; }
        public required List<HarnessChannel> Right { get; init; }
        public required List<HarnessChannel> Union { get; init; }
    }

    private static async Task<LoadedSession> Load(string sessionPath)
    {
        VirtualCrossoverProjectFile project =
            VirtualCrossoverProjectFile.LoadFrom(sessionPath);

        var left = new List<HarnessChannel>();
        var right = new List<HarnessChannel>();
        char letter = 'A';
        foreach (VirtualCrossoverChannelPairSettings pair in project.Pairs)
        {
            string blockName = letter.ToString();
            letter++;
            HarnessChannel? l = await LoadSide(pair.Left, pair.Mono
                ? $"{blockName} (mono)" : $"{blockName} L", pair.Mono);
            if (l != null)
            {
                left.Add(l);
                if (pair.Mono)
                {
                    right.Add(l);
                }
            }
            if (!pair.Mono)
            {
                HarnessChannel? r = await LoadSide(pair.Right, $"{blockName} R", false);
                if (r != null)
                {
                    right.Add(r);
                    if (l != null)
                    {
                        l.Peer = r;
                        r.Peer = l;
                    }
                }
            }
        }

        return new LoadedSession
        {
            Project = project,
            Left = left,
            Right = right,
            Union = left.Concat(right).Distinct().ToList()
        };
    }

    private static async Task<HarnessChannel?> LoadSide(
        VirtualCrossoverChannelSettings settings, string name, bool mono)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.SourceFilePath))
        {
            return null;
        }
        if (!RecordCache.TryGetValue(settings.SourceFilePath,
            out (Complex[] Ir, int SampleRate) record))
        {
            ImpulseResponseFile file =
                await ImpulseResponseFile.LoadAsync(settings.SourceFilePath);
            Complex[]? transfer = file.GetTransferImpulseResponse();
            if (transfer == null)
            {
                return null;
            }
            record = (transfer, file.SampleRate);
            RecordCache[settings.SourceFilePath] = record;
        }
        return new HarnessChannel
        {
            Name = name,
            SampleRate = record.SampleRate,
            Settings = settings,
            MeasuredIr = record.Ir,
            Mono = mono
        };
    }

    // Mirror VirtualCrossoverPanel.CleanCrosstalkHeads + reprocessor creation.
    // Chains are read from the CURRENT settings, so callers may mutate the
    // session's crossover settings before calling this.
    private static AlignmentReprocessor BuildReprocessor(
        LoadedSession session, StringBuilder log)
    {
        List<AlignmentReprocessInput> inputs = session.Union.Select(channel =>
        {
            var input = new AlignmentReprocessInput(
                channel, channel.MeasuredIr, channel.SampleRate,
                channel.Settings.ToChain());
            double[] real = Array.ConvertAll(
                input.MeasuredImpulseResponse, sample => sample.Real);
            CrosstalkHeadGate? gate = TransferIrDiagnostics.DetectCrosstalkHead(
                real, input.SampleRate);
            if (gate is not { } convicted)
            {
                return input;
            }
            log.AppendLine(
                $"{channel.Name}: playback-crosstalk click at " +
                $"{convicted.BurstTimeMs:0.00} ms ({convicted.BurstPeakDbReMax:0.0} dB " +
                "re max) removed");
            return input with
            {
                MeasuredImpulseResponse = TransferIrDiagnostics.CleanCrosstalkHead(
                    input.MeasuredImpulseResponse, input.SampleRate, convicted)
            };
        }).ToList();
        return new AlignmentReprocessor(inputs, 65_536, 8_192);
    }

    private static Dictionary<IAlignmentChannel, AlignmentOverride> RunStereo(
        LoadedSession session, StringBuilder log)
    {
        AlignmentReprocessor reprocessor = BuildReprocessor(session, log);
        IReadOnlyList<AlignmentSnapshot> initialSnapshots = reprocessor.Reprocess(
            new Dictionary<IAlignmentChannel, AlignmentOverride>());
        Dictionary<HarnessChannel, AlignmentSnapshot> initial = session.Union
            .Select((channel, i) => (channel, snapshot: initialSnapshots[i]))
            .ToDictionary(item => item.channel, item => item.snapshot);

        List<AlignmentSnapshot> ByBand(List<HarnessChannel> side) => side
            .OrderBy(channel => VirtualCrossoverJunctions.BandCenterHz(channel.Settings))
            .Select(channel => initial[channel])
            .ToList();
        List<AlignmentJunction> Pairs(List<AlignmentSnapshot> byBand)
        {
            var pairs = new List<AlignmentJunction>();
            for (int i = 0; i < byBand.Count - 1; i++)
            {
                double pairHz = VirtualCrossoverJunctions.GetPairCrossoverHz(
                    ((HarnessChannel)byBand[i].Channel).Settings,
                    ((HarnessChannel)byBand[i + 1].Channel).Settings);
                (double bandLowHz, double bandHighHz) =
                    VirtualCrossoverJunctions.OverlapBand(pairHz);
                pairs.Add(new AlignmentJunction(
                    byBand[i], byBand[i + 1], pairHz, bandLowHz, bandHighHz));
            }
            return pairs;
        }

        var pairLinks = new List<StereoPairLink>();
        foreach (HarnessChannel r in session.Right.Where(channel => !channel.Mono))
        {
            if (r.Peer is not { } l)
            {
                continue;
            }
            (double leftLow, double leftHigh) =
                VirtualCrossoverJunctions.GetChannelBand(l.Settings);
            (double rightLow, double rightHigh) =
                VirtualCrossoverJunctions.GetChannelBand(r.Settings);
            double lowHz = Math.Max(leftLow, rightLow);
            double highHz = Math.Min(leftHigh, rightHigh);
            if (highHz >= lowHz * VirtualCrossoverAnalysis.MinimumArrivalBandRatio)
            {
                pairLinks.Add(new StereoPairLink(l, r, lowHz, highHz));
            }
        }

        HarnessChannel bridgeLeft = session.Left
            .OrderBy(channel => VirtualCrossoverJunctions.BandCenterHz(channel.Settings))
            .Last();
        HarnessChannel bridgeRight = bridgeLeft.Peer!;
        (double blLow, double blHigh) =
            VirtualCrossoverJunctions.GetChannelBand(bridgeLeft.Settings);
        (double brLow, double brHigh) =
            VirtualCrossoverJunctions.GetChannelBand(bridgeRight.Settings);

        var alignment = new Dictionary<IAlignmentChannel, AlignmentOverride>();
        var decisions = new Dictionary<IAlignmentChannel, AlignmentDecision>();
        List<AlignmentSnapshot> leftByBand = ByBand(session.Left);
        List<AlignmentSnapshot> rightByBand = ByBand(session.Right);
        AutoAlignmentEngine.ComputeStereo(
            new StereoAlignmentPlan(
                leftByBand,
                Pairs(leftByBand),
                rightByBand,
                Pairs(rightByBand),
                session.Union.Where(channel => channel.Mono)
                    .Cast<IAlignmentChannel>()
                    .ToList(),
                bridgeLeft,
                bridgeRight,
                Math.Max(blLow, brLow),
                Math.Min(blHigh, brHigh),
                session.Project.StereoSceneOffsetMs,
                pairLinks),
            reprocessor.Reprocess,
            alignment,
            log,
            decisions);

        foreach (HarnessChannel channel in session.Union)
        {
            AlignmentOverride over = alignment.GetValueOrDefault(channel);
            log.AppendLine(
                $"Result {channel.Name}: delay {over.DelayMs:0.00}, " +
                $"invert {(over.InvertPolarity ? "yes" : "no")}");
        }
        return alignment;
    }

    [Fact]
    public async Task ReplaySessions()
    {
        if (!Directory.Exists(V3Dir))
        {
            return;
        }
        Directory.CreateDirectory(OutDir);
        foreach ((string path, string label) in new[]
        {
            (Path.Combine(V3Dir, "virtual-dsp-session_bad.json"), "v3-bad"),
            (Path.Combine(V3Dir, "virtual-dsp-session_bad2.json"), "v3-bad2"),
            (Path.Combine(V3Dir, "virtual-dsp-session.json"), "v3-good"),
            (Path.Combine(V2Dir, "virtual-dsp-session.json"), "head90"),
        })
        {
            LoadedSession session = await Load(path);
            var log = new StringBuilder();
            RunStereo(session, log);
            File.WriteAllText(
                Path.Combine(OutDir, $"replay-fixed-{label}.log"), log.ToString());
        }
    }

    // Gain sensitivity probe: the same session run with the mono sub at its
    // stored gain and at 0 dB — everything else identical. Dumps both logs
    // for a decision-level diff.
    [Fact]
    public async Task ProbeSubGainSensitivity()
    {
        if (!Directory.Exists(V3Dir))
        {
            return;
        }
        Directory.CreateDirectory(OutDir);
        foreach (double gainDb in new[] { -10.0, 0.0 })
        {
            LoadedSession session = await Load(
                Path.Combine(V3Dir, "virtual-dsp-session.json"));
            HarnessChannel sub = session.Union.First(channel => channel.Mono);
            sub.Settings.GainDb = gainDb;
            var log = new StringBuilder();
            RunStereo(session, log);
            File.WriteAllText(
                Path.Combine(OutDir, $"gain-probe-{gainDb:0}.log"), log.ToString());
        }
    }

    // Sub-band structure of the A/B junction under level-matched scoring:
    // the mean loss over both midbass junctions for a sweep of the sub's
    // relative delay, per band, both polarities.
    [Fact]
    public async Task ProbeSubJunctionLandscape()
    {
        if (!Directory.Exists(V3Dir))
        {
            return;
        }
        Directory.CreateDirectory(OutDir);
        LoadedSession session = await Load(
            Path.Combine(V3Dir, "virtual-dsp-session.json"));
        var log = new StringBuilder();
        Dictionary<IAlignmentChannel, AlignmentOverride> alignment =
            RunStereo(session, log);
        AlignmentReprocessor reprocessor = BuildReprocessor(session, new StringBuilder());

        HarnessChannel sub = session.Union.First(channel => channel.Mono);
        HarnessChannel bl = session.Left.First(channel => channel.Name == "B L");
        HarnessChannel br = session.Right.First(channel => channel.Name == "B R");
        AlignmentOverride subOver = alignment.GetValueOrDefault(sub);

        var report = new StringBuilder(
            $"final sub {subOver.DelayMs:0.00}{(subOver.InvertPolarity ? " inv" : "")}, " +
            $"B L {alignment.GetValueOrDefault(bl).DelayMs:0.00}, " +
            $"B R {alignment.GetValueOrDefault(br).DelayMs:0.00}\n");
        foreach ((double lowHz, double highHz) in
            new[] { (40.0, 160.0), (40.0, 80.0), (80.0, 160.0) })
        {
            var scored = new List<(double DeltaMs, double Score, bool Invert)>();
            foreach (bool invert in new[] { false, true })
            {
                var trial = new Dictionary<IAlignmentChannel, AlignmentOverride>(alignment)
                {
                    [sub] = new AlignmentOverride(
                        subOver.DelayMs, subOver.InvertPolarity ^ invert)
                };
                IReadOnlyList<AlignmentSnapshot> current = reprocessor.Reprocess(trial);
                Complex[] IrOf(HarnessChannel channel) =>
                    current[session.Union.IndexOf(channel)].ImpulseResponse;
                var evaluators = new[] { bl, br }
                    .Select(neighbor => VirtualCrossoverAnalysis.SumLossEvaluator.Create(
                        IrOf(sub), new List<Complex[]> { IrOf(neighbor) },
                        sub.SampleRate, lowHz, highHz, levelMatch: true))
                    .Where(evaluator => evaluator != null)
                    .ToList();
                for (double delta = -8; delta <= 8.0001; delta += 0.05)
                {
                    double total = 0;
                    foreach (VirtualCrossoverAnalysis.SumLossEvaluator? evaluator in evaluators)
                    {
                        (double lossDb, double dipDb) = evaluator!.Evaluate(delta);
                        total += lossDb +
                            VirtualCrossoverAnalysis.DipExcessPenaltyWeight *
                            (dipDb - lossDb);
                    }
                    scored.Add((delta, total / evaluators.Count, invert));
                }
            }
            var minima = new List<(double DeltaMs, double Score, bool Invert)>();
            foreach ((double deltaMs, double score, bool invert) in
                scored.OrderByDescending(item => item.Score))
            {
                if (minima.All(m => Math.Abs(m.DeltaMs - deltaMs) > 1.0))
                {
                    minima.Add((deltaMs, score, invert));
                }
            }
            report.AppendLine($"{lowHz:0}-{highHz:0} Hz (delta = extra sub delay from final): " +
                string.Join("; ", minima.Take(5).Select(m =>
                    $"{m.DeltaMs:+0.00;-0.00}{(m.Invert ? " inv" : "")} ({m.Score:0.00} dB)")));
        }
        File.WriteAllText(Path.Combine(OutDir, "sub-junction-landscape.log"), report.ToString());
    }

    // Sub-band consistency probe of the mono co-move's lobe hop: for each
    // session, compare the APPLIED hop position of the mono sub against its
    // pre-hop (polish) position, scored per junction sub-band. A genuine hop
    // should win consistently across sub-bands; a comb/modal impostor wins
    // the full-band mean while losing a sub-band.
    [Fact]
    public async Task ProbeMonoHopSubBands()
    {
        if (!Directory.Exists(V3Dir))
        {
            return;
        }
        Directory.CreateDirectory(OutDir);
        var report = new StringBuilder();
        foreach ((string path, string label, double hopDeltaMs) in new[]
        {
            (Path.Combine(V3Dir, "virtual-dsp-session_bad2.json"), "v3-bad2", 5.77),
            (Path.Combine(V3Dir, "virtual-dsp-session.json"), "v3-good", -4.13),
        })
        {
            LoadedSession session = await Load(path);
            var log = new StringBuilder();
            Dictionary<IAlignmentChannel, AlignmentOverride> alignment =
                RunStereo(session, log);
            AlignmentReprocessor reprocessor = BuildReprocessor(session, new StringBuilder());

            HarnessChannel sub = session.Union.First(channel => channel.Mono);
            HarnessChannel bl = session.Left.First(channel => channel.Name == "B L");
            HarnessChannel br = session.Right.First(channel => channel.Name == "B R");
            AlignmentOverride hop = alignment.GetValueOrDefault(sub);
            var polish = new AlignmentOverride(
                Math.Round(hop.DelayMs - hopDeltaMs, 2), !hop.InvertPolarity);
            report.AppendLine(
                $"=== {label}: hop A={hop.DelayMs:0.00}{(hop.InvertPolarity ? " inv" : "")}, " +
                $"polish A={polish.DelayMs:0.00}{(polish.InvertPolarity ? " inv" : "")} ===");

            foreach ((string name, AlignmentOverride overRide) in
                new[] { ("hop   ", hop), ("polish", polish) })
            {
                var trial = new Dictionary<IAlignmentChannel, AlignmentOverride>(alignment)
                {
                    [sub] = overRide
                };
                IReadOnlyList<AlignmentSnapshot> current = reprocessor.Reprocess(trial);
                Complex[] IrOf(HarnessChannel channel) =>
                    current[session.Union.IndexOf(channel)].ImpulseResponse;
                foreach ((double lowHz, double highHz) in
                    new[] { (40.0, 160.0), (40.0, 80.0), (80.0, 160.0) })
                {
                    double total = 0;
                    var parts = new List<string>();
                    foreach (HarnessChannel neighbor in new[] { bl, br })
                    {
                        (double LossDb, double DipDb)? loss =
                            VirtualCrossoverAnalysis.MeasureSumLoss(
                                IrOf(sub),
                                new List<Complex[]> { IrOf(neighbor) },
                                sub.SampleRate, lowHz, highHz);
                        double score = loss is { } v
                            ? v.LossDb + VirtualCrossoverAnalysis.DipExcessPenaltyWeight *
                                (v.DipDb - v.LossDb)
                            : double.NaN;
                        parts.Add($"{neighbor.Name} {score:0.00}");
                        total += score;
                    }
                    report.AppendLine(
                        $"  {name} {lowHz:0}-{highHz:0} Hz: mean {total / 2:0.00} dB ({string.Join(", ", parts)})");
                }
            }
            report.AppendLine();
        }
        File.WriteAllText(Path.Combine(OutDir, "mono-hop-subbands.log"), report.ToString());
    }

    // The B/C crossover matrix on the v3 records: engine result vs the
    // independently swept junction-loss landscape, per config.
    [Fact]
    public async Task CrossoverMatrix()
    {
        if (!Directory.Exists(V3Dir))
        {
            return;
        }
        Directory.CreateDirectory(OutDir);
        var report = new StringBuilder(
            "midbassLP midSlope | engine C-B (pol) score | landscape optima (best first)\n");

        foreach (double midbassLpHz in new[] { 180.0, 220.0 })
        foreach (int midbassLpSlope in new[] { 24, 36 })
        foreach (double midHpHz in new[] { 150.0, 280.0 })
        foreach (int midHpSlope in new[] { 24, 36 })
        {
            LoadedSession session = await Load(
                Path.Combine(V3Dir, "virtual-dsp-session_bad.json"));
            foreach (VirtualCrossoverChannelPairSettings pair in session.Project.Pairs)
            {
                foreach (VirtualCrossoverChannelSettings side in
                    new[] { pair.Left, pair.Right })
                {
                    if (side.CrossoverKind == CrossoverKind.BandPass &&
                        side.LowPassEdge.FrequencyHz is 180 &&
                        side.HighPassEdge.FrequencyHz is 80)
                    {
                        side.LowPassEdge = side.LowPassEdge with
                        {
                            FrequencyHz = midbassLpHz,
                            SlopeDbPerOctave = midbassLpSlope
                        };
                    }
                    if (side.CrossoverKind == CrossoverKind.BandPass &&
                        side.HighPassEdge.FrequencyHz is 150)
                    {
                        side.HighPassEdge = side.HighPassEdge with
                        {
                            FrequencyHz = midHpHz,
                            SlopeDbPerOctave = midHpSlope
                        };
                    }
                }
            }

            var log = new StringBuilder();
            Dictionary<IAlignmentChannel, AlignmentOverride> alignment =
                RunStereo(session, log);
            string config =
                $"LP{midbassLpHz:0}/{midbassLpSlope} HP{midHpHz:0}/{midHpSlope}";
            File.WriteAllText(
                Path.Combine(OutDir,
                    $"matrix-{midbassLpHz:0}-{midbassLpSlope}-{midHpHz:0}-{midHpSlope}.log"),
                log.ToString());

            HarnessChannel bl = session.Left.First(channel => channel.Name == "B L");
            HarnessChannel cl = session.Left.First(channel => channel.Name == "C L");
            AlignmentOverride blOver = alignment.GetValueOrDefault(bl);
            AlignmentOverride clOver = alignment.GetValueOrDefault(cl);
            double engineDelta = clOver.DelayMs - blOver.DelayMs;
            bool engineInv = clOver.InvertPolarity != blOver.InvertPolarity;

            // Independent landscape: C L vs fixed B L over the pair band.
            var scratchLog = new StringBuilder();
            AlignmentReprocessor reprocessor = BuildReprocessor(session, scratchLog);
            IReadOnlyList<AlignmentSnapshot> snapshots = reprocessor.Reprocess(
                new Dictionary<IAlignmentChannel, AlignmentOverride>());
            AlignmentSnapshot blSnap = snapshots[session.Union.IndexOf(bl)];
            AlignmentSnapshot clSnap = snapshots[session.Union.IndexOf(cl)];
            AlignmentSnapshot clInvSnap = reprocessor.Reprocess(
                new Dictionary<IAlignmentChannel, AlignmentOverride>
                {
                    [cl] = new AlignmentOverride(0, true)
                })[session.Union.IndexOf(cl)];

            double fc = VirtualCrossoverJunctions.GetPairCrossoverHz(
                bl.Settings, cl.Settings);
            (double bandLow, double bandHigh) = VirtualCrossoverJunctions.OverlapBand(fc);
            VirtualCrossoverAnalysis.SumLossEvaluator? normal =
                VirtualCrossoverAnalysis.SumLossEvaluator.Create(
                    clSnap.ImpulseResponse,
                    new List<Complex[]> { blSnap.ImpulseResponse },
                    cl.SampleRate, bandLow, bandHigh);
            VirtualCrossoverAnalysis.SumLossEvaluator? invertedEval =
                VirtualCrossoverAnalysis.SumLossEvaluator.Create(
                    clInvSnap.ImpulseResponse,
                    new List<Complex[]> { blSnap.ImpulseResponse },
                    cl.SampleRate, bandLow, bandHigh);
            if (normal == null || invertedEval == null)
            {
                report.AppendLine($"{config}: no usable bins");
                continue;
            }

            double ScoreOf(double delayMs, bool invert)
            {
                (double lossDb, double dipDb) =
                    (invert ? invertedEval : normal).Evaluate(delayMs);
                return lossDb +
                    VirtualCrossoverAnalysis.DipExcessPenaltyWeight *
                    (dipDb - lossDb);
            }

            var scored = new List<(double DelayMs, double Score, bool Invert)>();
            for (double delay = -2; delay <= 18.0001; delay += 0.02)
            {
                scored.Add((delay, ScoreOf(delay, false), false));
                scored.Add((delay, ScoreOf(delay, true), true));
            }
            var minima = new List<(double DelayMs, double Score, bool Invert)>();
            foreach ((double delayMs, double score, bool invert) in
                scored.OrderByDescending(item => item.Score))
            {
                if (minima.All(m => Math.Abs(m.DelayMs - delayMs) > 0.8))
                {
                    minima.Add((delayMs, score, invert));
                }
            }

            double engineScore = ScoreOf(engineDelta, engineInv);
            string optima = string.Join("; ", minima.Take(4).Select(m =>
                $"{m.DelayMs:0.00}{(m.Invert ? " inv" : "")} ({m.Score:0.00} dB)"));
            report.AppendLine(
                $"{config}: engine {engineDelta:0.00}{(engineInv ? " inv" : "")} " +
                $"({engineScore:0.00} dB) | {optima}");
        }

        File.WriteAllText(Path.Combine(OutDir, "matrix-summary.log"), report.ToString());
    }
}
