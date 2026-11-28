// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using CsCheck;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

/// <summary>
/// The environment rules over generated telemetry. Around the idle window, each host CPU
/// sample may get a new value (sometimes NaN, infinite or negative), a time shift across the
/// window edges and any phase label. Every case goes through <c>samples.csv</c> as the loader
/// reads it, then through the comparison, and is checked against an oracle that recomputes
/// the idle CPU mean and the completeness counts from the timestamps and the recorded windows.
/// </summary>
[TestFixture]
public class Given_Generated_Telemetry
{
    private const string Role = "baseline";
    private const int Pair = 3;
    private const int FirstSecond = 20;
    private const int LastSecond = 50;

    private sealed record Perturbation(bool ChangeValue, double Value, int ShiftSeconds, int Label);

    private static readonly string[] _labels =
    [
        PerfBenchmarkMetrics.SettlePhase,
        PerfBenchmarkMetrics.IdlePhase,
        PerfBenchmarkMetrics.SidePhase,
    ];

    private static readonly Gen<double> _value = Gen.Frequency(
        (12, Gen.Double[0, 100]),
        (1, Gen.Const(double.NaN)),
        (1, Gen.Const(double.PositiveInfinity)),
        (1, Gen.Double[-100, -0.001])
    );

    private static readonly Gen<Perturbation[]> _perturbations = Gen.Select(
        Gen.Int[0, 3].Select(chance => chance == 0),
        _value,
        Gen.Frequency((3, Gen.Const(0)), (1, Gen.Int[-6, 6])),
        Gen.Int[0, _labels.Length - 1],
        (change, value, shift, label) => new Perturbation(change, value, shift, label)
    ).Array[LastSecond - FirstSecond];

    [Test]
    public void It_invalidates_exactly_the_telemetry_the_rules_reject()
    {
        PerfBenchmarkComparisonInput input = new ComparisonBuilder().Build();
        PerfBenchmarkSideEvidence original = input.Sides.Single(side =>
            side.Side.Pair == Pair && side.Side.Role == Role
        );
        PerfBenchmarkEnvironmentRules rules = input.Profile.Profile.Environment;

        _perturbations.Sample(perturbations =>
        {
            List<PerfBenchmarkSample> samples = Perturb(original.Samples, perturbations);
            bool unusable = samples.Exists(sample => !double.IsFinite(sample.Value) || sample.Value < 0);

            IReadOnlyList<PerfBenchmarkSample> parsed;
            try
            {
                parsed = PerfBenchmarkMetrics.ParseCsv(ComparisonBuilder.ToCsv(samples));
            }
            catch (PerfArtifactValidationException exception)
            {
                unusable.Should().BeTrue(string.Join(" ", exception.Errors));
                return;
            }

            unusable.Should().BeFalse("a non-finite or negative value must not load");
            PerfBenchmarkComparisonResult result = PerfBenchmarkComparison.Compare(
                input with
                {
                    Sides =
                    [
                        .. input.Sides.Select(side =>
                            side == original ? side with { Samples = parsed } : side
                        ),
                    ],
                }
            );

            string at = $"pair {Pair} {Role}: rule:";
            foreach (PerfBenchmarkPhaseWindow window in original.Environment.Phases)
            {
                double[] inWindow =
                [
                    .. parsed
                        .Where(sample =>
                            sample.Metric == PerfBenchmarkMetrics.HostCpuPercent
                            && sample.TimestampUtc >= window.StartUtc
                            && sample.TimestampUtc < window.EndUtc
                        )
                        .Select(sample => sample.Value),
                ];
                double expected =
                    (window.EndUtc - window.StartUtc).TotalSeconds / rules.HostSampleIntervalSeconds;
                bool incomplete = inWindow.Length < expected * (double)rules.MinCompletenessPercent / 100;
                Reported(result, $"{at} incomplete samples for host-cpu-percent in the {window.Name} phase")
                    .Should()
                    .Be(incomplete, $"{window.Name} holds {inWindow.Length} host CPU samples");

                if (window.Name == PerfBenchmarkMetrics.IdlePhase)
                {
                    bool busy =
                        inWindow.Length > 0 && inWindow.Average() > (double)rules.MaxIdleHostCpuPercent;
                    Reported(result, $"{at} idle host-cpu-percent averaged")
                        .Should()
                        .Be(busy, $"the idle window's host CPU is [{string.Join(", ", inWindow)}]");
                }
            }
        });
    }

    private static bool Reported(PerfBenchmarkComparisonResult result, string fragment) =>
        result.InvalidReasons.Any(reason => reason.Contains(fragment, StringComparison.Ordinal));

    /// <summary>
    /// Applies one perturbation to each host CPU sample taken FirstSecond..LastSecond seconds
    /// after the side started: the idle window and the edges of its neighbours.
    /// </summary>
    private static List<PerfBenchmarkSample> Perturb(
        IReadOnlyList<PerfBenchmarkSample> samples,
        Perturbation[] perturbations
    )
    {
        DateTimeOffset start = ComparisonBuilder.SideStart(Pair, Role);
        return
        [
            .. samples.Select(sample =>
            {
                int second = (int)(sample.TimestampUtc - start).TotalSeconds;
                if (
                    sample.Metric != PerfBenchmarkMetrics.HostCpuPercent
                    || second < FirstSecond
                    || second >= LastSecond
                )
                {
                    return sample;
                }

                Perturbation perturbation = perturbations[second - FirstSecond];
                return sample with
                {
                    TimestampUtc = sample.TimestampUtc.AddSeconds(perturbation.ShiftSeconds),
                    Phase = _labels[perturbation.Label],
                    Value = perturbation.ChangeValue ? perturbation.Value : sample.Value,
                };
            }),
        ];
    }
}
