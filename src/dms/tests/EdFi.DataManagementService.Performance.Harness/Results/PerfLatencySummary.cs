// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Latency distribution for one scenario cell, in milliseconds. Raw per-iteration samples are
/// retained so a later comparison can recompute any statistic rather than trusting these.
/// </summary>
public sealed record PerfLatencySummary(
    double P50Ms,
    double P95Ms,
    double MeanMs,
    double MinMs,
    double MaxMs,
    IReadOnlyList<double> SamplesMs
);
