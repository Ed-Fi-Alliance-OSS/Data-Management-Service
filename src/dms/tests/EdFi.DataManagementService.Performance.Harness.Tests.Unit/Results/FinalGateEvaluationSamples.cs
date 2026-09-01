// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

/// <summary>
/// Canned provider evidence for the evaluator: a smoke-scale baseline aligned with the
/// final-gate samples (same fixture identity, deep offset, commits, and environment), so the
/// unmodified whole passes every gate and individual tests can break one rule at a time.
/// </summary>
internal static class FinalGateEvaluationSamples
{
    public static PerfBaselineRunArtifacts Baseline(string provider = "postgresql") =>
        new(
            ResultSamples.Manifest(provider) with
            {
                Fixture = new PerfManifestFixture("smoke-10k", 10_000, FinalGateResultSamples.DeepOffset),
            },
            provider == "postgresql" ? ResultSamples.PostgresqlDocument() : ResultSamples.MssqlDocument(),
            $"/evidence/{provider}-baseline"
        );

    public static PerfFinalGateRunArtifacts Primary(string provider = "postgresql") =>
        new(
            FinalGateResultSamples.PrimaryManifest(provider),
            FinalGateResultSamples.PrimaryDocument(provider),
            $"/evidence/{provider}-final-primary"
        );

    public static PerfFinalGateRunArtifacts Descriptors(string provider = "postgresql") =>
        new(
            FinalGateResultSamples.DescriptorManifest(provider),
            FinalGateResultSamples.DescriptorDocument(provider),
            $"/evidence/{provider}-final-descriptors"
        );

    public static PerfFinalGateProviderEvidence Evidence(string provider = "postgresql") =>
        new(Baseline(provider), Primary(provider), Descriptors(provider));

    /// <summary>
    /// Replaces the latency of every row the predicate matches with a constant-sample
    /// summary, so a test can push one cell over a ratio limit while the retained samples
    /// still recompute to their statistics.
    /// </summary>
    public static PerfFinalGateResultsDocument WithRowLatency(
        PerfFinalGateResultsDocument document,
        Func<PerfFinalGateScenarioResult, bool> match,
        double milliseconds
    ) =>
        document with
        {
            Results =
            [
                .. document.Results.Select(row =>
                    match(row)
                        ? row with
                        {
                            LatencyMs = PerfLatencyMeasurement.Summarize([
                                .. Enumerable.Repeat(milliseconds, row.MeasuredIterations),
                            ]),
                        }
                        : row
                ),
            ],
        };
}
