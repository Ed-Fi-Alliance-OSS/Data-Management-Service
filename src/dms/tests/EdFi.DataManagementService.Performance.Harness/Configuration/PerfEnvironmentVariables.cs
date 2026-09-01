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
    public const string DocumentCacheProvider = "PERF_DOCUMENTCACHE_PROVIDER";
    public const string DocumentCachePageSize = "PERF_DOCUMENTCACHE_PAGE_SIZE";
    public const string DocumentCacheHighWaterMark = "PERF_DOCUMENTCACHE_HIGH_WATER_MARK";
    public const string DocumentCacheProjectorConcurrency = "PERF_DOCUMENTCACHE_PROJECTOR_CONCURRENCY";
    public const string DocumentCacheWarmupStatusSamples = "PERF_DOCUMENTCACHE_WARMUP_STATUS_SAMPLES";
    public const string DocumentCacheMeasuredStatusSamples = "PERF_DOCUMENTCACHE_MEASURED_STATUS_SAMPLES";
    public const string DocumentCacheOutageWrites = "PERF_DOCUMENTCACHE_OUTAGE_WRITES";
    public const string DocumentCacheSameDocumentContenders = "PERF_DOCUMENTCACHE_SAME_DOCUMENT_CONTENDERS";
    public const string OperatorNote = "PERF_OPERATOR_NOTE";
}
