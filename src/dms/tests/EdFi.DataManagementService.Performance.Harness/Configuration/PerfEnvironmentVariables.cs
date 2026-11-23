// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// Environment variable names the performance harness reads its run configuration from.
/// </summary>
public static class PerfEnvironmentVariables
{
    public const string ResultsDirectory = "PERF_RESULTS_DIR";
    public const string RunnerCommit = "PERF_RUNNER_COMMIT";
    public const string Fixture = "PERF_FIXTURE";
    public const string WarmupIterations = "PERF_WARMUP_ITERATIONS";
    public const string MeasuredIterations = "PERF_MEASURED_ITERATIONS";
    public const string DeepOffset = "PERF_DEEP_OFFSET";
    public const string ImageTag = "PERF_IMAGE_TAG";
    public const string ImageDigest = "PERF_IMAGE_DIGEST";
    public const string StorageNote = "PERF_STORAGE_NOTE";
    public const string AllowCi = "PERF_ALLOW_CI";
    public const string AllowedDirtyPrefixes = "PERF_ALLOW_DIRTY_PREFIXES";
    public const string DescriptorFixture = "PERF_DESCRIPTOR_FIXTURE";
    public const string ReportDirectory = "PERF_REPORT_DIR";
    public const string BaselineDirectoryPostgresql = "PERF_BASELINE_DIR_POSTGRESQL";
    public const string BaselineDirectoryMssql = "PERF_BASELINE_DIR_MSSQL";
    public const string FinalPrimaryDirectoryPostgresql = "PERF_FINAL_PRIMARY_DIR_POSTGRESQL";
    public const string FinalPrimaryDirectoryMssql = "PERF_FINAL_PRIMARY_DIR_MSSQL";
    public const string FinalDescriptorsDirectoryPostgresql = "PERF_FINAL_DESCRIPTORS_DIR_POSTGRESQL";
    public const string FinalDescriptorsDirectoryMssql = "PERF_FINAL_DESCRIPTORS_DIR_MSSQL";

    // Benchmark comparison (DMS-1019). The wrapper sets these for each side process.
    public const string BenchmarkRunId = "PERF_BENCHMARK_RUN_ID";
    public const string BenchmarkRole = "PERF_BENCHMARK_ROLE";
    public const string BenchmarkPair = "PERF_BENCHMARK_PAIR";
    public const string BenchmarkPosition = "PERF_BENCHMARK_POSITION";

    /// <summary>
    /// "workload" (the evidence workload) or "smokeWorkload" from the benchmark profile.
    /// </summary>
    public const string BenchmarkWorkload = "PERF_BENCHMARK_WORKLOAD";

    /// <summary>
    /// The comparison directory the report reads.
    /// </summary>
    public const string BenchmarkDirectory = "PERF_BENCHMARK_DIR";

    /// <summary>
    /// Test-only slowdown, set on candidate sides only: &lt;scenarioId&gt;=&lt;factor&gt;;…
    /// </summary>
    public const string InjectSlowdown = "PERF_INJECT_SLOWDOWN";
}
