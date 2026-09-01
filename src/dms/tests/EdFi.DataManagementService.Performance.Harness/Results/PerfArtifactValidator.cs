// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Structural validation of a run's artifacts: the run manifest and its results document.
/// Every rule guards against the harness reporting work it did not actually do — incomplete
/// cells, impossible statistics, mismatched provenance, or unredacted environment detail.
/// Timing values are never judged; only completeness and internal consistency are.
/// </summary>
public static partial class PerfArtifactValidator
{
    public static void EnsureValid(PerfRunManifest manifest, PerfResultsDocument document)
    {
        IReadOnlyList<string> errors = Validate(manifest, document);
        if (errors.Count > 0)
        {
            throw new PerfArtifactValidationException(errors);
        }
    }

    public static IReadOnlyList<string> Validate(PerfRunManifest manifest, PerfResultsDocument document)
    {
        List<string> errors = [];

        if (manifest is null)
        {
            errors.Add("manifest: manifest is required.");
        }
        else
        {
            ValidateManifest(manifest, errors);
        }

        if (document is null)
        {
            errors.Add("results: results document is required.");
        }
        else
        {
            ValidateResults(document, manifest, errors);
        }

        return errors;
    }

    private static void ValidateManifest(PerfRunManifest manifest, List<string> errors)
    {
        if (manifest.SchemaVersion != PerfArtifactSchema.Version)
        {
            errors.Add(
                $"manifest: schema version '{manifest.SchemaVersion}' must be '{PerfArtifactSchema.Version}'."
            );
        }

        ValidateRunIdentity(manifest.Run, errors);
        ValidateCommits(manifest.Commits, errors);
        ValidateFixture(manifest.Fixture, errors);
        ValidateIterationPlan(manifest.Iterations, manifest.Fixture, errors);
        ValidateEnvironment(manifest.Environment, errors);
    }

    internal static void ValidateRunIdentity(PerfRunIdentity? run, List<string> errors)
    {
        if (run is null)
        {
            errors.Add("manifest: run identity is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(run.RunId))
        {
            errors.Add("manifest: run id is required.");
        }

        if (!IsCanonicalProvider(run.Provider))
        {
            errors.Add($"manifest: provider '{run.Provider}' must be the canonical 'postgresql' or 'mssql'.");
        }

        bool parsable =
            !string.IsNullOrWhiteSpace(run.CapturedAtUtc)
            && DateTimeOffset.TryParse(
                run.CapturedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _
            );
        if (!parsable || !run.CapturedAtUtc.EndsWith('Z'))
        {
            errors.Add(
                $"manifest: captured-at '{run.CapturedAtUtc}' must be an ISO-8601 UTC timestamp ending in 'Z'."
            );
        }
    }

    internal static void ValidateCommits(PerfCommitIdentity? commits, List<string> errors)
    {
        if (commits is null)
        {
            errors.Add("manifest: commit identity is required.");
            return;
        }

        if (!IsLowercaseHex(commits.RunnerCommit, 40))
        {
            errors.Add("manifest: runner commit must be 40 lowercase hex characters.");
        }

        if (!IsLowercaseHex(commits.SubjectCommit, 40))
        {
            errors.Add("manifest: subject commit must be 40 lowercase hex characters.");
        }

        if (commits.WorktreeDirtyPaths is null)
        {
            errors.Add("manifest: worktree dirty paths list is required, even when empty.");
        }
        else if (commits.WorktreeDirtyPaths.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("manifest: worktree dirty path entries must be non-blank.");
        }
    }

    private static void ValidateFixture(PerfManifestFixture? fixture, List<string> errors)
    {
        if (fixture is null)
        {
            errors.Add("manifest: fixture is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(fixture.FixtureId))
        {
            errors.Add("manifest: fixture id is required.");
            return;
        }

        PerfFixtureKind? kind = PerfFixtureKind.FindById(fixture.FixtureId);
        if (kind is null)
        {
            errors.Add(
                $"manifest: fixture id must be one of: "
                    + $"{string.Join(", ", PerfFixtureKind.All.Select(known => known.Id))}; got '{fixture.FixtureId}'."
            );
            return;
        }

        if (fixture.RowCount != kind.RowCount)
        {
            errors.Add(
                $"manifest: fixture row count {fixture.RowCount} must be {kind.RowCount} for '{kind.Id}'."
            );
        }

        if (!PerfRunConfigurationLoader.IsWithinDeepOffsetBounds(kind, fixture.DeepOffset))
        {
            errors.Add(
                $"manifest: deep offset {fixture.DeepOffset} must be between 0 and "
                    + $"{PerfRunConfigurationLoader.MaximumDeepOffset(kind)}."
            );
        }
    }

    private static void ValidateIterationPlan(
        PerfIterationPlan? iterations,
        PerfManifestFixture? fixture,
        List<string> errors
    )
    {
        if (iterations is null)
        {
            errors.Add("manifest: iteration plan is required.");
            return;
        }

        if (iterations.WarmupIterations < PerfRunConfigurationLoader.MinimumWarmupIterations)
        {
            errors.Add(
                $"manifest: warmup iterations must be at least "
                    + $"{PerfRunConfigurationLoader.MinimumWarmupIterations}; got {iterations.WarmupIterations}."
            );
        }

        if (iterations.MeasuredIterations < PerfRunConfigurationLoader.MinimumMeasuredIterations)
        {
            errors.Add(
                $"manifest: measured iterations must be at least "
                    + $"{PerfRunConfigurationLoader.MinimumMeasuredIterations}; got {iterations.MeasuredIterations}."
            );
        }

        IReadOnlyList<PerfExecutedCell>? cells = iterations.CellExecutionOrder;
        if (cells is not null && cells.Any(cell => cell is null))
        {
            errors.Add("manifest: cell execution order entries must be non-null.");
            cells = [.. cells.Where(cell => cell is not null)];
        }

        ValidateCellSet(
            "manifest: cell execution order",
            cells?.Select(cell => (cell.ScenarioId, cell.PageSize)),
            errors
        );

        if (cells is not null && fixture is not null)
        {
            foreach (PerfExecutedCell cell in cells)
            {
                long? expected = ExpectedOffset(cell.ScenarioId, cell.PageSize, fixture.DeepOffset);
                if (expected is not null && cell.Offset != expected)
                {
                    errors.Add(
                        $"manifest: cell {cell.ScenarioId}/{cell.PageSize} offset {cell.Offset} must be {expected}."
                    );
                }
            }
        }
    }

    internal static void ValidateEnvironment(PerfEnvironmentIdentity? environment, List<string> errors)
    {
        if (environment is null)
        {
            errors.Add("manifest: environment identity is required.");
            return;
        }

        PerfServerIdentity? server = environment.Server;
        if (server is null)
        {
            errors.Add("manifest: server identity is required.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(server.ServerVersion))
            {
                errors.Add("manifest: server version is required.");
            }

            if (string.IsNullOrWhiteSpace(server.ImageTag))
            {
                errors.Add("manifest: image tag is required.");
            }

            if (!DigestRegex().IsMatch(server.ImageDigest ?? string.Empty))
            {
                errors.Add("manifest: image digest must match sha256:<64 lowercase hex>.");
            }

            if (string.IsNullOrWhiteSpace(server.ConnectionStringShape))
            {
                errors.Add("manifest: connection string shape is required.");
            }
            else if (HasUnredactedSecret(server.ConnectionStringShape))
            {
                errors.Add(
                    "manifest: connection string shape must redact secrets; "
                        + "every password value must read REDACTED."
                );
            }

            if (string.IsNullOrWhiteSpace(server.StorageNote))
            {
                errors.Add("manifest: storage note is required.");
            }

            if (server.Settings is null || server.Settings.Count == 0)
            {
                errors.Add("manifest: at least one server setting is required.");
            }
            else
            {
                ValidateSettingEntries(
                    "manifest: server setting entries must have non-blank names and values.",
                    server.Settings,
                    errors
                );
            }
        }

        PerfHostIdentity? host = environment.Host;
        if (host is null)
        {
            errors.Add("manifest: host identity is required.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(host.OsDescription))
            {
                errors.Add("manifest: os description is required.");
            }

            if (string.IsNullOrWhiteSpace(host.ProcessArchitecture))
            {
                errors.Add("manifest: process architecture is required.");
            }

            if (string.IsNullOrWhiteSpace(host.DotnetVersion))
            {
                errors.Add("manifest: dotnet version is required.");
            }

            if (string.IsNullOrWhiteSpace(host.CpuModel))
            {
                errors.Add("manifest: cpu model is required.");
            }

            if (host.LogicalCores <= 0)
            {
                errors.Add("manifest: logical cores must be positive.");
            }

            if (host.TotalMemoryBytes <= 0)
            {
                errors.Add("manifest: total memory bytes must be positive.");
            }

            if (string.IsNullOrWhiteSpace(host.MachineFingerprint))
            {
                errors.Add("manifest: machine fingerprint is required.");
            }
        }

        if (environment.DriverVersions is null || environment.DriverVersions.Count == 0)
        {
            errors.Add("manifest: at least one driver version is required.");
        }
        else
        {
            ValidateSettingEntries(
                "manifest: driver version entries must have non-blank names and values.",
                environment.DriverVersions,
                errors
            );
        }
    }

    private static void ValidateSettingEntries(
        string message,
        IReadOnlyList<PerfSetting> settings,
        List<string> errors
    )
    {
        bool anyInvalid = settings.Any(setting =>
            setting is null
            || string.IsNullOrWhiteSpace(setting.Name)
            || string.IsNullOrWhiteSpace(setting.Value)
        );
        if (anyInvalid)
        {
            errors.Add(message);
        }
    }

    private static void ValidateResults(
        PerfResultsDocument document,
        PerfRunManifest? manifest,
        List<string> errors
    )
    {
        if (document.SchemaVersion != PerfArtifactSchema.Version)
        {
            errors.Add(
                $"results: schema version '{document.SchemaVersion}' must be '{PerfArtifactSchema.Version}'."
            );
        }

        if (document.Results is null || document.Results.Count == 0)
        {
            errors.Add("results: at least one result row is required.");
            return;
        }

        ValidateCellSet(
            "results",
            document
                .Results.Where(result => result is not null)
                .Select(result => (result.ScenarioId, result.PageSize)),
            errors
        );

        for (int index = 0; index < document.Results.Count; index++)
        {
            PerfScenarioResult row = document.Results[index];
            if (row is null)
            {
                errors.Add($"results[{index}]: row is required.");
                continue;
            }

            ValidateRow(row, index, manifest, errors);
        }
    }

    private static void ValidateRow(
        PerfScenarioResult row,
        int index,
        PerfRunManifest? manifest,
        List<string> errors
    )
    {
        string at = $"results[{index}]";

        if (!IsCanonicalProvider(row.Provider))
        {
            errors.Add($"{at}: provider '{row.Provider}' must be the canonical 'postgresql' or 'mssql'.");
        }

        PerfRunIdentity? run = manifest?.Run;
        if (run is not null && row.Provider != run.Provider)
        {
            errors.Add($"{at}: provider '{row.Provider}' must match the run provider '{run.Provider}'.");
        }

        if (!PerfScenarios.IsKnown(row.ScenarioId))
        {
            errors.Add($"{at}: unknown scenario id '{row.ScenarioId}'.");
        }

        if (!PerfScenarios.PageSizes.Contains(row.PageSize))
        {
            errors.Add($"{at}: page size {row.PageSize} is not in the measured matrix.");
        }

        PerfManifestFixture? fixture = manifest?.Fixture;
        if (fixture is not null)
        {
            long? expected = ExpectedOffset(row.ScenarioId, row.PageSize, fixture.DeepOffset);
            if (expected is not null && row.Offset != expected)
            {
                errors.Add(
                    $"{at}: offset {row.Offset} must be {expected} for scenario '{row.ScenarioId}' "
                        + $"at page size {row.PageSize}."
                );
            }
        }

        if (row.ReturnedRows != row.PageSize)
        {
            errors.Add($"{at}: returned rows {row.ReturnedRows} must equal page size {row.PageSize}.");
        }

        if (row.CommandCountPerRequest != 1)
        {
            errors.Add($"{at}: command count per request must be 1; got {row.CommandCountPerRequest}.");
        }

        PerfIterationPlan? iterations = manifest?.Iterations;
        if (iterations is not null)
        {
            if (row.WarmupIterations != iterations.WarmupIterations)
            {
                errors.Add(
                    $"{at}: warmup iterations {row.WarmupIterations} must match the manifest's "
                        + $"{iterations.WarmupIterations}."
                );
            }

            if (row.MeasuredIterations != iterations.MeasuredIterations)
            {
                errors.Add(
                    $"{at}: measured iterations {row.MeasuredIterations} must match the manifest's "
                        + $"{iterations.MeasuredIterations}."
                );
            }
        }

        ValidateLatency(at, "latency", row.LatencyMs, row.MeasuredIterations, errors);
        ValidateLatency(at, "driver execute", row.DriverExecuteMs, row.MeasuredIterations, errors);
        ValidateCommit(at, "runner commit", row.RunnerCommit, manifest?.Commits?.RunnerCommit, errors);
        ValidateCommit(at, "subject commit", row.SubjectCommit, manifest?.Commits?.SubjectCommit, errors);

        if (!IsLowercaseHex(row.PageSelectionSqlSha256, 64))
        {
            errors.Add($"{at}: page selection SQL hash must be 64 lowercase hex characters.");
        }

        if (string.IsNullOrWhiteSpace(row.PlanFile))
        {
            errors.Add($"{at}: plan file is required.");
        }

        ValidateDatabaseMetrics(at, row, errors);
    }

    internal static void ValidateLatency(
        string at,
        string layer,
        PerfLatencySummary? latency,
        int measuredIterations,
        List<string> errors
    )
    {
        if (latency is null)
        {
            errors.Add($"{at}: {layer} summary is required.");
            return;
        }

        int sampleCount = latency.SamplesMs?.Count ?? 0;
        if (sampleCount != measuredIterations)
        {
            errors.Add(
                $"{at}: {layer} sample count {sampleCount} must equal measured iterations {measuredIterations}."
            );
        }

        bool ordered =
            latency.MinMs <= latency.P50Ms
            && latency.P50Ms <= latency.P95Ms
            && latency.P95Ms <= latency.MaxMs;
        if (!ordered)
        {
            errors.Add($"{at}: {layer} percentiles must satisfy min <= p50 <= p95 <= max.");
        }

        // The summary statistics must be reproducible from the retained samples with the
        // same nearest-rank semantics that produced them; a summary the samples cannot
        // explain is not evidence. The relative tolerance is pure guard band against
        // floating-point round-trip and summation-order noise — any real tamper is orders
        // of magnitude larger.
        if (latency.SamplesMs is { Count: > 0 } samples)
        {
            PerfLatencySummary recomputed = PerfLatencyMeasurement.Summarize(samples);
            AddIfNotRecomputable(at, layer, "min", latency.MinMs, recomputed.MinMs, errors);
            AddIfNotRecomputable(at, layer, "max", latency.MaxMs, recomputed.MaxMs, errors);
            AddIfNotRecomputable(at, layer, "p50", latency.P50Ms, recomputed.P50Ms, errors);
            AddIfNotRecomputable(at, layer, "p95", latency.P95Ms, recomputed.P95Ms, errors);
            AddIfNotRecomputable(at, layer, "mean", latency.MeanMs, recomputed.MeanMs, errors);
        }
    }

    private static void AddIfNotRecomputable(
        string at,
        string layer,
        string statistic,
        double stored,
        double recomputed,
        List<string> errors
    )
    {
        if (Math.Abs(stored - recomputed) > Math.Abs(recomputed) * 1e-9)
        {
            errors.Add(
                $"{at}: {layer} {statistic} {stored} must match {recomputed} recomputed from the samples."
            );
        }
    }

    internal static void ValidateCommit(
        string at,
        string role,
        string value,
        string? manifestValue,
        List<string> errors
    )
    {
        if (!IsLowercaseHex(value, 40))
        {
            errors.Add($"{at}: {role} must be 40 lowercase hex characters.");
        }
        else if (manifestValue is not null && value != manifestValue)
        {
            errors.Add($"{at}: {role} must match the manifest.");
        }
    }

    private static void ValidateDatabaseMetrics(string at, PerfScenarioResult row, List<string> errors) =>
        ValidateDatabaseMetricsSide(at, row.Provider, row.Database, errors);

    internal static void ValidateDatabaseMetricsSide(
        string at,
        string provider,
        PerfDatabaseMetrics? metrics,
        List<string> errors
    )
    {
        if (metrics is null)
        {
            errors.Add($"{at}: database metrics are required.");
            return;
        }

        bool postgresqlSide =
            metrics.BuffersHit is not null
            && metrics.BuffersRead is not null
            && metrics.DbExecutionMs is not null;
        bool sqlServerSide =
            metrics.LogicalReads is not null
            && metrics.PhysicalReads is not null
            && metrics.DbCpuMs is not null
            && metrics.DbElapsedMs is not null;
        bool anySqlServerValue =
            metrics.LogicalReads is not null
            || metrics.PhysicalReads is not null
            || metrics.DbCpuMs is not null
            || metrics.DbElapsedMs is not null;
        bool anyPostgresqlValue =
            metrics.BuffersHit is not null
            || metrics.BuffersRead is not null
            || metrics.DbExecutionMs is not null;

        if (provider == PerfProviders.ArtifactName(PerfProvider.Postgresql))
        {
            if (!postgresqlSide)
            {
                errors.Add(
                    $"{at}: postgresql database metrics (buffers hit/read, execution ms) are required."
                );
            }

            if (anySqlServerValue)
            {
                errors.Add($"{at}: sql server metrics must be absent on a postgresql row.");
            }
        }
        else if (provider == PerfProviders.ArtifactName(PerfProvider.Mssql))
        {
            if (!sqlServerSide)
            {
                errors.Add($"{at}: sql server database metrics (reads, cpu/elapsed ms) are required.");
            }

            if (anyPostgresqlValue)
            {
                errors.Add($"{at}: postgresql metrics must be absent on a sql server row.");
            }
        }
    }

    private static void ValidateCellSet(
        string prefix,
        IEnumerable<(string ScenarioId, int PageSize)>? cells,
        List<string> errors
    )
    {
        if (cells is null)
        {
            errors.Add($"{prefix}: cells are required.");
            return;
        }

        List<(string ScenarioId, int PageSize)> observed = [.. cells];
        int expectedCount = PerfScenarios.AllIds.Count * PerfScenarios.PageSizes.Count;
        if (observed.Count != expectedCount)
        {
            errors.Add($"{prefix}: must contain exactly {expectedCount} cells; got {observed.Count}.");
        }

        foreach (var duplicate in observed.GroupBy(cell => cell).Where(group => group.Count() > 1))
        {
            errors.Add($"{prefix}: duplicate cell {duplicate.Key.ScenarioId}/{duplicate.Key.PageSize}.");
        }

        foreach (string scenarioId in PerfScenarios.AllIds)
        {
            foreach (int pageSize in PerfScenarios.PageSizes)
            {
                if (!observed.Contains((scenarioId, pageSize)))
                {
                    errors.Add($"{prefix}: missing cell {scenarioId}/{pageSize}.");
                }
            }
        }
    }

    private static long? ExpectedOffset(string scenarioId, int pageSize, long deepOffset) =>
        scenarioId switch
        {
            PerfScenarios.TraditionalOffsetZero => 0,
            PerfScenarios.TraditionalOffsetShallow => pageSize,
            PerfScenarios.TraditionalOffsetDeep => deepOffset,
            _ => null,
        };

    private static readonly string[] _canonicalProviders =
    [
        PerfProviders.ArtifactName(PerfProvider.Postgresql),
        PerfProviders.ArtifactName(PerfProvider.Mssql),
    ];

    // Canonical lowercase artifact names are required rather than anything PerfProviders.Parse
    // accepts, because the metric-side rules and cross-artifact comparisons match on exact
    // strings; a mixed-case name would sail past both without an error.
    internal static bool IsCanonicalProvider(string? providerName) =>
        providerName is not null && _canonicalProviders.Contains(providerName);

    private static bool HasUnredactedSecret(string connectionStringShape) =>
        SecretValueRegex()
            .Matches(connectionStringShape)
            .Any(match => match.Groups[2].Value.Trim() != "REDACTED");

    internal static bool IsLowercaseHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(IsLowercaseHexDigit);

    private static bool IsLowercaseHexDigit(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestRegex();

    [GeneratedRegex(@"\b(password|pwd)\s*=\s*([^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValueRegex();
}
