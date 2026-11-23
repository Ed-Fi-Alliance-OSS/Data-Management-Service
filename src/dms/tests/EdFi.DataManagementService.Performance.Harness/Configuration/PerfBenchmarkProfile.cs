// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// The closed benchmark workload: the six traditional paging cells plus the serial Student
/// collection update. Bump <see cref="WorkloadDefinitionVersion" /> whenever what a scenario
/// measures changes; the profile must then declare the new version.
/// </summary>
public static class PerfBenchmarkScenarios
{
    public const string WorkloadDefinitionVersion = "1.0.0";

    public const string StudentPutCollectionUpdate = "student-put-collection-update";

    public static string ReadCellId(string scenarioId, int pageSize) => $"{scenarioId}/{pageSize}";

    public static readonly IReadOnlyList<string> AllIds =
    [
        .. PerfScenarios.AllIds.SelectMany(scenarioId =>
            PerfScenarios.PageSizes.Select(pageSize => ReadCellId(scenarioId, pageSize))
        ),
        StudentPutCollectionUpdate,
    ];
}

/// <summary>
/// The versioned benchmark profile (<c>Configuration/benchmark-profile.json</c>): the workload,
/// the comparison and detection rules, the environment rules, and the per-provider qualified
/// environment fingerprints. A profile at version 1.0.0 or later is frozen.
/// </summary>
public sealed record PerfBenchmarkProfile(
    string ProfileVersion,
    string WorkloadDefinitionVersion,
    PerfBenchmarkSupportedRange SupportedRange,
    string ApprovedReferenceCommit,
    PerfBenchmarkWorkload Workload,
    PerfBenchmarkSmokeWorkload SmokeWorkload,
    PerfBenchmarkRequiredEnvironment RequiredEnvironment,
    PerfBenchmarkComparisonRules Comparison,
    PerfBenchmarkDetectionRules Detection,
    PerfBenchmarkEnvironmentRules Environment
)
{
    [JsonIgnore]
    public bool IsFrozen => Version.Parse(ProfileVersion).Major >= 1;
}

public sealed record PerfBenchmarkSupportedRange(string FloorCommit);

public sealed record PerfBenchmarkIterations(int WarmupIterations, int MeasuredIterations);

/// <summary>
/// The evidence workload. Everything beyond the fixture, offset and iteration counts is a
/// required value every side must report exactly.
/// </summary>
public sealed record PerfBenchmarkWorkload(
    string Fixture,
    long DeepOffset,
    PerfBenchmarkIterations Reads,
    PerfBenchmarkIterations Writes,
    int Concurrency,
    string Authorization,
    string LogLevel,
    string ConnectionRegime,
    IReadOnlyDictionary<string, PerfBenchmarkConnectionSettings> Connection
);

/// <summary>
/// The non-evidence workload for mechanics checks and rehearsals, with fixed rehearsal
/// injection factors so the rehearsal exercises the frozen-factor path.
/// </summary>
public sealed record PerfBenchmarkSmokeWorkload(
    string Fixture,
    long DeepOffset,
    PerfBenchmarkIterations Reads,
    PerfBenchmarkIterations Writes,
    IReadOnlyDictionary<string, decimal?> InjectionFactors
);

/// <summary>
/// Effective connection settings. The PostgreSQL-only members are absent for SQL Server.
/// </summary>
public sealed record PerfBenchmarkConnectionSettings(
    bool Pooling,
    int MinPoolSize,
    int MaxPoolSize,
    bool? NoResetOnClose = null,
    int? AutoPrepareMinUsages = null,
    int? MaxAutoPrepare = null
);

public sealed record PerfBenchmarkRequiredEnvironment(
    string OsFamily,
    string Architecture,
    bool AcPowerOnline,
    string DatabaseStorageMountType
);

/// <summary>
/// Thresholds are fractions (0.10 is 10%), keyed by provider, then scenario.
/// </summary>
public sealed record PerfBenchmarkComparisonRules(
    int Pairs,
    string Rule,
    bool P95WarningsEnabled,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> P50Thresholds
);

/// <summary>
/// The frozen detection acceptance rule. Increases, tolerance, margin and cap are fractions;
/// injection factors are keyed by provider, then demonstration scenario, and stay null until
/// sized.
/// </summary>
public sealed record PerfBenchmarkDetectionRules(
    IReadOnlyList<string> Scenarios,
    decimal TargetIncrease,
    decimal Tolerance,
    decimal Margin,
    decimal CapAboveTarget,
    int RequiredRepeats,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal?>> InjectionFactors
);

public sealed record PerfBenchmarkEnvironmentRules(
    int SettleSeconds,
    int IdleSeconds,
    int HostSampleIntervalSeconds,
    int DockerSampleIntervalSeconds,
    decimal MaxIdleHostCpuPercent,
    decimal MaxIdleDatabaseCpuPercent,
    long MinHostAvailableMemoryMb,
    decimal MaxDatabaseMemoryPercentOfLimit,
    decimal MinCompletenessPercent,
    IReadOnlyDictionary<string, PerfBenchmarkEnvironmentFingerprint?> QualifiedEnvironments
);

/// <summary>
/// A provider's qualified environment: host, platform, allocation and database facts only.
/// It never holds the profile hash or the harness commit, so freezing the profile cannot
/// invalidate it.
/// </summary>
public sealed record PerfBenchmarkEnvironmentFingerprint(
    string OsFamily,
    string Architecture,
    string MachineFingerprint,
    string CpuModel,
    int LogicalCores,
    long TotalMemoryBytes,
    string PowerPlan,
    bool AcPowerOnline,
    int DockerVmCpus,
    long DockerVmMemoryBytes,
    decimal DatabaseCpuLimit,
    long DatabaseMemoryLimitBytes,
    string ImageDigest,
    string ServerVersion,
    IReadOnlyList<PerfSetting> DatabaseSettings,
    string StorageMountType,
    PerfBenchmarkConnectionSettings Connection
);

public sealed record PerfLoadedBenchmarkProfile(PerfBenchmarkProfile Profile, string Sha256);

/// <summary>
/// Strict loader for the benchmark profile: unknown, missing, duplicate or null members are
/// rejected, then every rule is validated and all errors are reported at once.
/// </summary>
public static partial class PerfBenchmarkProfileLoader
{
    public const string FileName = "benchmark-profile.json";

    public const int MinimumWriteWarmupIterations = 5;

    /// <summary>
    /// Below 20 samples, nearest-rank p95 is the maximum.
    /// </summary>
    public const int MinimumWriteMeasuredIterations = 20;

    public const decimal MaximumInjectionFactor = 5m;

    public const string MedianOfPairP50Ratios = "median-of-pair-p50-ratios";

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
    };

    private static readonly IReadOnlyList<string> _providers =
    [
        PerfProviders.ArtifactName(PerfProvider.Postgresql),
        PerfProviders.ArtifactName(PerfProvider.Mssql),
    ];

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Configuration", FileName);

    public static PerfLoadedBenchmarkProfile LoadFile(string path) => Load(File.ReadAllBytes(path));

    public static PerfLoadedBenchmarkProfile Load(byte[] utf8Json)
    {
        PerfBenchmarkProfile profile;
        try
        {
            profile =
                JsonSerializer.Deserialize<PerfBenchmarkProfile>(utf8Json, _options)
                ?? throw new JsonException("The profile is null.");
        }
        catch (JsonException exception)
        {
            throw new PerfConfigurationException([$"{FileName}: {exception.Message}"]);
        }

        List<string> errors = Validate(profile);
        if (errors.Count > 0)
        {
            throw new PerfConfigurationException([.. errors.Select(error => $"{FileName}: {error}")]);
        }

        return new PerfLoadedBenchmarkProfile(profile, Convert.ToHexStringLower(SHA256.HashData(utf8Json)));
    }

    private static List<string> Validate(PerfBenchmarkProfile profile)
    {
        List<string> errors = [];

        if (!SemanticVersion().IsMatch(profile.ProfileVersion))
        {
            errors.Add($"profileVersion must be MAJOR.MINOR.PATCH; got '{profile.ProfileVersion}'.");
        }

        if (profile.WorkloadDefinitionVersion != PerfBenchmarkScenarios.WorkloadDefinitionVersion)
        {
            errors.Add(
                $"workloadDefinitionVersion must be '{PerfBenchmarkScenarios.WorkloadDefinitionVersion}'; "
                    + $"got '{profile.WorkloadDefinitionVersion}'."
            );
        }

        RequireCommit("supportedRange.floorCommit", profile.SupportedRange.FloorCommit, errors);
        RequireCommit("approvedReferenceCommit", profile.ApprovedReferenceCommit, errors);

        ValidateWorkload(profile.Workload, errors);
        ValidateSmokeWorkload(profile.SmokeWorkload, profile.Detection.Scenarios, errors);
        ValidateComparison(profile.Comparison, errors);
        ValidateDetection(profile, errors);
        ValidateEnvironment(profile, errors);

        return errors;
    }

    private static void ValidateWorkload(PerfBenchmarkWorkload workload, List<string> errors)
    {
        ValidateFixtureAndIterations(
            "workload",
            workload.Fixture,
            workload.DeepOffset,
            workload.Reads,
            workload.Writes,
            errors
        );

        RequireEqual("workload.concurrency", workload.Concurrency, 1, errors);
        RequireEqual("workload.authorization", workload.Authorization, "bypassed", errors);
        RequireEqual("workload.logLevel", workload.LogLevel, "Warning", errors);
        RequireEqual("workload.connectionRegime", workload.ConnectionRegime, "ownership-published", errors);

        foreach (
            string provider in RequireExactKeys(
                "workload.connection",
                workload.Connection,
                _providers,
                errors
            )
        )
        {
            ValidateConnection(
                $"workload.connection.{provider}",
                provider,
                workload.Connection[provider],
                errors
            );
        }
    }

    private static void ValidateSmokeWorkload(
        PerfBenchmarkSmokeWorkload smoke,
        IReadOnlyList<string> demonstrationScenarios,
        List<string> errors
    )
    {
        ValidateFixtureAndIterations(
            "smokeWorkload",
            smoke.Fixture,
            smoke.DeepOffset,
            smoke.Reads,
            smoke.Writes,
            errors
        );

        const string Path = "smokeWorkload.injectionFactors";
        foreach (
            string scenario in RequireExactKeys(Path, smoke.InjectionFactors, demonstrationScenarios, errors)
        )
        {
            ValidateFactor($"{Path}.{scenario}", smoke.InjectionFactors[scenario], "", errors);
        }
    }

    private static void ValidateFixtureAndIterations(
        string path,
        string fixtureId,
        long deepOffset,
        PerfBenchmarkIterations reads,
        PerfBenchmarkIterations writes,
        List<string> errors
    )
    {
        PerfFixtureKind? fixture = PerfFixtureKind.FindById(fixtureId);
        if (fixture is null)
        {
            errors.Add($"{path}.fixture: unknown fixture '{fixtureId}'.");
        }
        else if (!PerfRunConfigurationLoader.IsWithinDeepOffsetBounds(fixture, deepOffset))
        {
            errors.Add(
                $"{path}.deepOffset must be between 0 and "
                    + $"{PerfRunConfigurationLoader.MaximumDeepOffset(fixture)} for fixture '{fixture.Id}'; got {deepOffset}."
            );
        }

        RequireAtLeast(
            $"{path}.reads.warmupIterations",
            reads.WarmupIterations,
            PerfRunConfigurationLoader.MinimumWarmupIterations,
            errors
        );
        RequireAtLeast(
            $"{path}.reads.measuredIterations",
            reads.MeasuredIterations,
            PerfRunConfigurationLoader.MinimumMeasuredIterations,
            errors
        );
        RequireAtLeast(
            $"{path}.writes.warmupIterations",
            writes.WarmupIterations,
            MinimumWriteWarmupIterations,
            errors
        );
        RequireAtLeast(
            $"{path}.writes.measuredIterations",
            writes.MeasuredIterations,
            MinimumWriteMeasuredIterations,
            errors
        );
    }

    private static void ValidateConnection(
        string path,
        string provider,
        PerfBenchmarkConnectionSettings settings,
        List<string> errors
    )
    {
        if (
            settings.MinPoolSize < 0
            || settings.MaxPoolSize < 1
            || settings.MinPoolSize > settings.MaxPoolSize
        )
        {
            errors.Add(
                $"{path}: pool sizes must satisfy 0 <= minPoolSize <= maxPoolSize and maxPoolSize >= 1."
            );
        }

        bool hasPostgresqlSettings =
            settings.NoResetOnClose is not null
            && settings.AutoPrepareMinUsages is not null
            && settings.MaxAutoPrepare is not null;
        bool hasAnyPostgresqlSetting =
            settings.NoResetOnClose is not null
            || settings.AutoPrepareMinUsages is not null
            || settings.MaxAutoPrepare is not null;

        if (provider == PerfProviders.ArtifactName(PerfProvider.Postgresql) && !hasPostgresqlSettings)
        {
            errors.Add($"{path}: noResetOnClose, autoPrepareMinUsages and maxAutoPrepare are required.");
        }
        else if (provider == PerfProviders.ArtifactName(PerfProvider.Mssql) && hasAnyPostgresqlSetting)
        {
            errors.Add(
                $"{path}: noResetOnClose, autoPrepareMinUsages and maxAutoPrepare apply only to postgresql."
            );
        }
    }

    private static void ValidateComparison(PerfBenchmarkComparisonRules comparison, List<string> errors)
    {
        RequireAtLeast("comparison.pairs", comparison.Pairs, 2, errors);
        RequireEqual("comparison.rule", comparison.Rule, MedianOfPairP50Ratios, errors);

        const string Path = "comparison.p50Thresholds";
        foreach (string provider in RequireExactKeys(Path, comparison.P50Thresholds, _providers, errors))
        {
            IReadOnlyDictionary<string, decimal> thresholds = comparison.P50Thresholds[provider];
            foreach (
                string scenario in RequireExactKeys(
                    $"{Path}.{provider}",
                    thresholds,
                    PerfBenchmarkScenarios.AllIds,
                    errors
                )
            )
            {
                if (thresholds[scenario] <= 0)
                {
                    errors.Add($"{Path}.{provider}.{scenario} must be positive; got {thresholds[scenario]}.");
                }
            }
        }
    }

    private static void ValidateDetection(PerfBenchmarkProfile profile, List<string> errors)
    {
        PerfBenchmarkDetectionRules detection = profile.Detection;

        if (
            detection.Scenarios.Count == 0
            || detection.Scenarios.Distinct().Count() != detection.Scenarios.Count
        )
        {
            errors.Add("detection.scenarios must be a non-empty list of distinct scenarios.");
        }

        foreach (
            string scenario in detection.Scenarios.Where(scenario =>
                !PerfBenchmarkScenarios.AllIds.Contains(scenario)
            )
        )
        {
            errors.Add($"detection.scenarios: unknown scenario '{scenario}'.");
        }

        if (detection.TargetIncrease <= 0)
        {
            errors.Add($"detection.targetIncrease must be positive; got {detection.TargetIncrease}.");
        }

        if (detection.Tolerance < 0 || detection.Margin < 0 || detection.CapAboveTarget < 0)
        {
            errors.Add(
                "detection.tolerance, detection.margin and detection.capAboveTarget must not be negative."
            );
        }

        RequireAtLeast("detection.requiredRepeats", detection.RequiredRepeats, 1, errors);

        // A demonstration threshold must leave the full margin under the band's lower edge,
        // so a threshold can never quietly rise to the declared target.
        decimal maximumDemonstrationThreshold =
            detection.TargetIncrease - detection.Tolerance - detection.Margin;
        foreach (
            (string provider, IReadOnlyDictionary<string, decimal> thresholds) in profile
                .Comparison
                .P50Thresholds
        )
        {
            foreach (string scenario in detection.Scenarios)
            {
                if (
                    thresholds.TryGetValue(scenario, out decimal threshold)
                    && threshold > maximumDemonstrationThreshold
                )
                {
                    errors.Add(
                        $"comparison.p50Thresholds.{provider}.{scenario} is a demonstration threshold and must be "
                            + $"at most {maximumDemonstrationThreshold} (target - tolerance - margin); got {threshold}."
                    );
                }
            }
        }

        const string Path = "detection.injectionFactors";
        foreach (string provider in RequireExactKeys(Path, detection.InjectionFactors, _providers, errors))
        {
            IReadOnlyDictionary<string, decimal?> factors = detection.InjectionFactors[provider];
            foreach (
                string scenario in RequireExactKeys(
                    $"{Path}.{provider}",
                    factors,
                    detection.Scenarios,
                    errors
                )
            )
            {
                ValidateFactor(
                    $"{Path}.{provider}.{scenario}",
                    factors[scenario],
                    profile.IsFrozenSafe() ? " once the profile is frozen (version 1.0.0 or later)" : null,
                    errors
                );
            }
        }
    }

    private static void ValidateEnvironment(PerfBenchmarkProfile profile, List<string> errors)
    {
        PerfBenchmarkEnvironmentRules environment = profile.Environment;

        RequireAtLeast("environment.settleSeconds", environment.SettleSeconds, 1, errors);
        RequireAtLeast("environment.idleSeconds", environment.IdleSeconds, 1, errors);
        RequireAtLeast(
            "environment.hostSampleIntervalSeconds",
            environment.HostSampleIntervalSeconds,
            1,
            errors
        );
        RequireAtLeast(
            "environment.dockerSampleIntervalSeconds",
            environment.DockerSampleIntervalSeconds,
            1,
            errors
        );
        RequirePercent("environment.maxIdleHostCpuPercent", environment.MaxIdleHostCpuPercent, errors);
        RequirePercent(
            "environment.maxIdleDatabaseCpuPercent",
            environment.MaxIdleDatabaseCpuPercent,
            errors
        );
        RequirePercent(
            "environment.maxDatabaseMemoryPercentOfLimit",
            environment.MaxDatabaseMemoryPercentOfLimit,
            errors
        );
        RequirePercent("environment.minCompletenessPercent", environment.MinCompletenessPercent, errors);
        RequireAtLeast(
            "environment.minHostAvailableMemoryMb",
            environment.MinHostAvailableMemoryMb,
            1,
            errors
        );

        const string Path = "environment.qualifiedEnvironments";
        foreach (
            string provider in RequireExactKeys(Path, environment.QualifiedEnvironments, _providers, errors)
        )
        {
            PerfBenchmarkEnvironmentFingerprint? fingerprint = environment.QualifiedEnvironments[provider];
            if (fingerprint is not null)
            {
                ValidateFingerprint($"{Path}.{provider}", provider, fingerprint, profile, errors);
            }
            else if (profile.IsFrozenSafe())
            {
                errors.Add(
                    $"{Path}.{provider} is required once the profile is frozen (version 1.0.0 or later)."
                );
            }
        }
    }

    private static void ValidateFingerprint(
        string path,
        string provider,
        PerfBenchmarkEnvironmentFingerprint fingerprint,
        PerfBenchmarkProfile profile,
        List<string> errors
    )
    {
        foreach (
            (string member, string value) in new[]
            {
                ("osFamily", fingerprint.OsFamily),
                ("architecture", fingerprint.Architecture),
                ("machineFingerprint", fingerprint.MachineFingerprint),
                ("cpuModel", fingerprint.CpuModel),
                ("powerPlan", fingerprint.PowerPlan),
                ("serverVersion", fingerprint.ServerVersion),
            }.Where(text => string.IsNullOrWhiteSpace(text.Item2))
        )
        {
            errors.Add($"{path}.{member} must not be blank.");
        }

        RequireAtLeast($"{path}.logicalCores", fingerprint.LogicalCores, 1, errors);
        RequireAtLeast($"{path}.totalMemoryBytes", fingerprint.TotalMemoryBytes, 1, errors);
        RequireAtLeast($"{path}.dockerVmCpus", fingerprint.DockerVmCpus, 1, errors);
        RequireAtLeast($"{path}.dockerVmMemoryBytes", fingerprint.DockerVmMemoryBytes, 1, errors);
        RequireAtLeast($"{path}.databaseMemoryLimitBytes", fingerprint.DatabaseMemoryLimitBytes, 1, errors);

        if (fingerprint.DatabaseCpuLimit < 0)
        {
            errors.Add($"{path}.databaseCpuLimit must not be negative (0 means unlimited).");
        }

        if (!ImageDigest().IsMatch(fingerprint.ImageDigest))
        {
            errors.Add(
                $"{path}.imageDigest must be sha256:<64 lowercase hex>; got '{fingerprint.ImageDigest}'."
            );
        }

        if (
            fingerprint.DatabaseSettings.Count == 0
            || fingerprint.DatabaseSettings.Select(setting => setting.Name).Distinct().Count()
                != fingerprint.DatabaseSettings.Count
        )
        {
            errors.Add($"{path}.databaseSettings must be a non-empty list with distinct names.");
        }

        // A fingerprint is only ever derived from a run that satisfied every required value.
        PerfBenchmarkRequiredEnvironment required = profile.RequiredEnvironment;
        RequireEqual($"{path}.osFamily", fingerprint.OsFamily, required.OsFamily, errors);
        RequireEqual($"{path}.architecture", fingerprint.Architecture, required.Architecture, errors);
        RequireEqual($"{path}.acPowerOnline", fingerprint.AcPowerOnline, required.AcPowerOnline, errors);
        RequireEqual(
            $"{path}.storageMountType",
            fingerprint.StorageMountType,
            required.DatabaseStorageMountType,
            errors
        );

        if (
            profile.Workload.Connection.TryGetValue(provider, out PerfBenchmarkConnectionSettings? pinned)
            && fingerprint.Connection != pinned
        )
        {
            errors.Add($"{path}.connection must equal workload.connection.{provider}.");
        }
    }

    /// <summary>
    /// A null factor is allowed only when <paramref name="requiredWhen" /> is null; otherwise it
    /// completes the error message.
    /// </summary>
    private static void ValidateFactor(
        string path,
        decimal? factor,
        string? requiredWhen,
        List<string> errors
    )
    {
        if (factor is null)
        {
            if (requiredWhen is not null)
            {
                errors.Add($"{path} is required{requiredWhen}.");
            }

            return;
        }

        if (factor <= 0 || factor > MaximumInjectionFactor)
        {
            errors.Add($"{path} must be in (0, {MaximumInjectionFactor}]; got {factor}.");
        }
    }

    /// <summary>
    /// Reports missing and unknown keys, and returns the expected keys that are present so
    /// their values can be validated.
    /// </summary>
    private static IEnumerable<string> RequireExactKeys<T>(
        string path,
        IReadOnlyDictionary<string, T> values,
        IReadOnlyList<string> expectedKeys,
        List<string> errors
    )
    {
        foreach (string missing in expectedKeys.Where(key => !values.ContainsKey(key)))
        {
            errors.Add($"{path}: missing '{missing}'.");
        }

        foreach (string unknown in values.Keys.Where(key => !expectedKeys.Contains(key)))
        {
            errors.Add($"{path}: unknown key '{unknown}'.");
        }

        return expectedKeys.Where(values.ContainsKey);
    }

    private static void RequireCommit(string path, string value, List<string> errors)
    {
        if (!Commit().IsMatch(value))
        {
            errors.Add($"{path} must be a 40-character lowercase hex commit SHA; got '{value}'.");
        }
    }

    private static void RequireEqual<T>(string path, T actual, T expected, List<string> errors)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            errors.Add($"{path} must be '{expected}'; got '{actual}'.");
        }
    }

    private static void RequireAtLeast(string path, long value, long minimum, List<string> errors)
    {
        if (value < minimum)
        {
            errors.Add($"{path} must be at least {minimum}; got {value}.");
        }
    }

    private static void RequirePercent(string path, decimal value, List<string> errors)
    {
        if (value <= 0 || value > 100)
        {
            errors.Add($"{path} must be in (0, 100]; got {value}.");
        }
    }

    /// <summary>
    /// Frozen-ness for validation, tolerating a malformed version (already reported).
    /// </summary>
    private static bool IsFrozenSafe(this PerfBenchmarkProfile profile) =>
        SemanticVersion().IsMatch(profile.ProfileVersion) && profile.IsFrozen;

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex SemanticVersion();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Commit();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex ImageDigest();
}
