// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// A validated harness run configuration. Construct through
/// <see cref="PerfRunConfigurationLoader" /> so every value has passed validation.
/// </summary>
public sealed record PerfRunConfiguration(
    string ResultsDirectory,
    string RunnerCommit,
    PerfFixtureKind Fixture,
    int WarmupIterations,
    int MeasuredIterations,
    long DeepOffset
);
