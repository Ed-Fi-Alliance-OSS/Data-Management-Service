// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Performance.Harness.Configuration;

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
        ValidateManifest(manifest, errors);
        ValidateResults(document, manifest, errors);
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

    private static void ValidateRunIdentity(PerfRunIdentity? run, List<string> errors)
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

        if (!IsKnownProvider(run.Provider))
        {
            errors.Add($"manifest: unknown provider '{run.Provider}'.");
        }

        bool parsable = DateTimeOffset.TryParse(
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

    private static void ValidateCommits(PerfCommitIdentity? commits, List<string> errors)
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

        long maximumDeepOffset = kind.RowCount - PerfScenarios.MaximumPageSize;
        if (fixture.DeepOffset < 0 || fixture.DeepOffset > maximumDeepOffset)
        {
            errors.Add(
                $"manifest: deep offset {fixture.DeepOffset} must be between 0 and {maximumDeepOffset}."
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

        ValidateCellSet(
            "manifest: cell execution order",
            iterations.CellExecutionOrder?.Select(cell => (cell.ScenarioId, cell.PageSize)),
            errors
        );

        if (iterations.CellExecutionOrder is not null && fixture is not null)
        {
            foreach (PerfExecutedCell cell in iterations.CellExecutionOrder)
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

    private static void ValidateEnvironment(PerfEnvironmentIdentity? environment, List<string> errors)
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
        }

        PerfHostIdentity? host = environment.Host;
        if (host is null)
        {
            errors.Add("manifest: host identity is required.");
        }
        else
        {
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
    }

    private static void ValidateResults(
        PerfResultsDocument document,
        PerfRunManifest manifest,
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
            document.Results.Select(result => (result.ScenarioId, result.PageSize)),
            errors
        );

        for (int index = 0; index < document.Results.Count; index++)
        {
            ValidateRow(document.Results[index], index, manifest, errors);
        }
    }

    private static void ValidateRow(
        PerfScenarioResult row,
        int index,
        PerfRunManifest manifest,
        List<string> errors
    )
    {
        string at = $"results[{index}]";

        if (manifest.Run is not null && row.Provider != manifest.Run.Provider)
        {
            errors.Add(
                $"{at}: provider '{row.Provider}' must match the run provider '{manifest.Run.Provider}'."
            );
        }

        if (!PerfScenarios.IsKnown(row.ScenarioId))
        {
            errors.Add($"{at}: unknown scenario id '{row.ScenarioId}'.");
        }

        if (!PerfScenarios.PageSizes.Contains(row.PageSize))
        {
            errors.Add($"{at}: page size {row.PageSize} is not in the measured matrix.");
        }

        if (manifest.Fixture is not null)
        {
            long? expected = ExpectedOffset(row.ScenarioId, row.PageSize, manifest.Fixture.DeepOffset);
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

        if (manifest.Iterations is not null)
        {
            if (row.WarmupIterations != manifest.Iterations.WarmupIterations)
            {
                errors.Add(
                    $"{at}: warmup iterations {row.WarmupIterations} must match the manifest's "
                        + $"{manifest.Iterations.WarmupIterations}."
                );
            }

            if (row.MeasuredIterations != manifest.Iterations.MeasuredIterations)
            {
                errors.Add(
                    $"{at}: measured iterations {row.MeasuredIterations} must match the manifest's "
                        + $"{manifest.Iterations.MeasuredIterations}."
                );
            }
        }

        ValidateLatency(at, "latency", row.LatencyMs, row.MeasuredIterations, errors);
        ValidateLatency(at, "db command", row.DbCommandMs, row.MeasuredIterations, errors);
        ValidateCommit(at, "runner commit", row.RunnerCommit, manifest.Commits?.RunnerCommit, errors);
        ValidateCommit(at, "subject commit", row.SubjectCommit, manifest.Commits?.SubjectCommit, errors);

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

    private static void ValidateLatency(
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
    }

    private static void ValidateCommit(
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

    private static void ValidateDatabaseMetrics(string at, PerfScenarioResult row, List<string> errors)
    {
        PerfDatabaseMetrics? metrics = row.Database;
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

        if (row.Provider == PerfProviders.ArtifactName(PerfProvider.Postgresql))
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
        else if (row.Provider == PerfProviders.ArtifactName(PerfProvider.Mssql))
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

    private static bool IsKnownProvider(string providerName)
    {
        try
        {
            PerfProviders.Parse(providerName ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasUnredactedSecret(string connectionStringShape) =>
        SecretValueRegex()
            .Matches(connectionStringShape)
            .Any(match => match.Groups[2].Value.Trim() != "REDACTED");

    private static bool IsLowercaseHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(IsLowercaseHexDigit);

    private static bool IsLowercaseHexDigit(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestRegex();

    [GeneratedRegex(@"\b(password|pwd)\s*=\s*([^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValueRegex();
}
