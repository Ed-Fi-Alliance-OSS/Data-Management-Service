// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Which side of which pair this process measures, as the wrapper declares it.
/// </summary>
public sealed record PerfBenchmarkSideIdentity(string RunId, string Role, int Pair, int Position);

/// <summary>
/// What one side runs: the provider, the profile and which of its workloads, the side's
/// identity, the slowdown spec (none on baseline sides), the directory every artifact of the
/// side goes to, and the harness (runner) commit.
/// </summary>
public sealed record PerfBenchmarkSidePlan(
    PerfProvider Provider,
    PerfLoadedBenchmarkProfile Profile,
    bool Smoke,
    PerfBenchmarkSideIdentity Identity,
    PerfSlowdownSpec Injection,
    string SideDirectory,
    string RunnerCommit
)
{
    public PerfBenchmarkSideWorkload Workload =>
        Smoke
            ? new(
                PerfProviders.ArtifactName(Provider),
                Profile.Profile.SmokeWorkload.Fixture,
                Profile.Profile.SmokeWorkload.DeepOffset,
                Profile.Profile.SmokeWorkload.Reads,
                Profile.Profile.SmokeWorkload.Writes
            )
            : new(
                PerfProviders.ArtifactName(Provider),
                Profile.Profile.Workload.Fixture,
                Profile.Profile.Workload.DeepOffset,
                Profile.Profile.Workload.Reads,
                Profile.Profile.Workload.Writes
            );

    public PerfFixtureDefinition FixtureDefinition =>
        new(
            PerfFixtureKind.FindById(Workload.Fixture)
                ?? throw new PerfConfigurationException([$"Unknown fixture '{Workload.Fixture}'."])
        );

    /// <summary>
    /// Reads the plan the wrapper exports to the side process.
    /// </summary>
    public static PerfBenchmarkSidePlan FromEnvironment(
        PerfProvider provider,
        Func<string, string?> readVariable
    )
    {
        List<string> errors = [];
        string? Read(string name)
        {
            string? value = readVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        int ReadNumber(string name, int minimum, int maximum)
        {
            string? text = Read(name);
            if (
                text is null
                || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < minimum
                || value > maximum
            )
            {
                errors.Add($"{name} must be an integer in [{minimum}, {maximum}]; got '{text}'.");
                return minimum;
            }

            return value;
        }

        string? sideDirectory = PerfRunConfigurationLoader.ReadFullyQualifiedDirectory(
            PerfEnvironmentVariables.ResultsDirectory,
            Read,
            errors
        );
        string? runnerCommit = PerfRunConfigurationLoader.ReadRunnerCommit(Read, errors);
        string? runId = Read(PerfEnvironmentVariables.BenchmarkRunId);
        if (runId is null)
        {
            errors.Add($"{PerfEnvironmentVariables.BenchmarkRunId} is required.");
        }

        string? role = Read(PerfEnvironmentVariables.BenchmarkRole);
        if (role is not (PerfBenchmarkSideResult.BaselineRole or PerfBenchmarkSideResult.CandidateRole))
        {
            errors.Add(
                $"{PerfEnvironmentVariables.BenchmarkRole} must be 'baseline' or 'candidate'; got '{role}'."
            );
        }

        int pair = ReadNumber(PerfEnvironmentVariables.BenchmarkPair, 1, 99);
        int position = ReadNumber(PerfEnvironmentVariables.BenchmarkPosition, 1, 2);

        string workload = Read(PerfEnvironmentVariables.BenchmarkWorkload) ?? "workload";
        if (workload is not ("workload" or "smokeWorkload"))
        {
            errors.Add(
                $"{PerfEnvironmentVariables.BenchmarkWorkload} must be 'workload' or 'smokeWorkload'; got '{workload}'."
            );
        }

        PerfSlowdownSpec injection = PerfSlowdownSpec.None;
        try
        {
            injection = PerfSlowdownSpec.Parse(Read(PerfEnvironmentVariables.InjectSlowdown));
        }
        catch (PerfConfigurationException exception)
        {
            errors.AddRange(exception.Errors);
        }

        if (errors.Count > 0)
        {
            throw new PerfConfigurationException(errors);
        }

        return new PerfBenchmarkSidePlan(
            provider,
            PerfBenchmarkProfileLoader.LoadFile(PerfBenchmarkProfileLoader.DefaultPath),
            workload == "smokeWorkload",
            new PerfBenchmarkSideIdentity(runId!, role!, pair, position),
            injection,
            sideDirectory!,
            runnerCommit!
        );
    }
}

/// <summary>
/// The side's required values, captured after ownership is published and before any read.
/// </summary>
public sealed record PerfBenchmarkSideFacts(
    PerfBenchmarkConnectionSettings EffectiveConnection,
    string EffectiveSchemaHash,
    string EffectiveLogLevel,
    bool OwnershipPublished,
    PerfBenchmarkInstrumentation Instrumentation
)
{
    /// <summary>
    /// Recorded when the side failed before its facts were captured.
    /// </summary>
    public static readonly PerfBenchmarkSideFacts Unknown = new(
        new PerfBenchmarkConnectionSettings(false, 0, 0),
        string.Empty,
        string.Empty,
        false,
        new PerfBenchmarkInstrumentation(false, false)
    );
}

/// <summary>
/// The write phase's result with the set-based verification before and after the writes.
/// </summary>
public sealed record PerfBenchmarkWriteOutcome(
    PerfWriteScenarioResult Result,
    IReadOnlyList<string> MismatchesBeforeWrites,
    IReadOnlyList<string> MismatchesAfterWrites
);

/// <summary>
/// The side's live work, as delegates so the pipeline's ordering and failure accounting can
/// be tested without a host or database.
/// </summary>
public sealed record PerfBenchmarkSideSteps(
    Func<Task<PerfBenchmarkSideFacts>> PrepareAsync,
    Func<IPerfReadObserver, Task<string>> RunReadsAsync,
    Func<PerfWriteOutcomes, Task<PerfBenchmarkWriteOutcome>> RunWritesAsync,
    Func<IReadOnlyDictionary<string, int>> AffectedRequests,
    Func<PerfWindowStamp>? Clock = null
);

public sealed record PerfBenchmarkSideOutcome(
    PerfBenchmarkSideResult Result,
    string ReadRunDirectory,
    PerfBenchmarkWriteOutcome Writes
);

/// <summary>
/// One benchmark side: publish ownership and capture the required values, run the six read
/// cells through the paging pipeline with the read observer, run the serial write, then
/// write <c>benchmark-side.json</c>. On any failure it still writes the side result, with
/// the failing phase, the reason and the exact counts, and rethrows.
/// </summary>
public static class PerfBenchmarkSidePipeline
{
    public static async Task<PerfBenchmarkSideOutcome> RunAsync(
        PerfBenchmarkSidePlan plan,
        PerfBenchmarkSideSteps steps
    )
    {
        PerfBenchmarkSideWorkload workload = plan.Workload;
        PerfBenchmarkReadObserver observer = new(
            workload.DeepOffset,
            workload.Reads.WarmupIterations,
            workload.Reads.MeasuredIterations,
            steps.Clock
        );
        PerfWriteOutcomes outcomes = new(
            workload.Writes.WarmupIterations,
            workload.Writes.MeasuredIterations,
            steps.Clock
        );
        PerfBenchmarkSideFacts? facts = null;
        PerfBenchmarkReadRunReference? readRun = null;
        bool writing = false;

        try
        {
            facts = await steps.PrepareAsync();
            string readRunDirectory = await steps.RunReadsAsync(observer);
            // Pin the paging run now, so a side that fails during the writes still names it.
            readRun = PerfBenchmarkReadRunReference.Of(readRunDirectory);

            writing = true;
            PerfBenchmarkWriteOutcome writes = await steps.RunWritesAsync(outcomes);
            if (writes.MismatchesBeforeWrites.Count == 0)
            {
                throw new PerfObservationException(
                    "Write verification passed before any write, so it cannot prove the writes happened."
                );
            }

            if (writes.MismatchesAfterWrites.Count > 0)
            {
                throw new PerfObservationException(
                    "Write verification failed: " + string.Join(" ", writes.MismatchesAfterWrites)
                );
            }

            PerfBenchmarkSideResult result = Build(
                plan,
                new PerfBenchmarkSideStatus(PerfBenchmarkSideStatus.Completed),
                facts,
                observer,
                writes.Result,
                steps.AffectedRequests(),
                readRun
            );
            Write(plan.SideDirectory, result);
            return new PerfBenchmarkSideOutcome(result, readRunDirectory, writes);
        }
        catch (Exception failure)
        {
            PerfBenchmarkSideResult failed = Build(
                plan,
                new PerfBenchmarkSideStatus(
                    PerfBenchmarkSideStatus.Failed,
                    FailedPhase(writing, observer.AllReadsCompleted),
                    failure.Message
                ),
                facts ?? PerfBenchmarkSideFacts.Unknown,
                observer,
                outcomes.ToResult(),
                steps.AffectedRequests(),
                readRun
            );

            try
            {
                Write(plan.SideDirectory, failed);
            }
            catch (Exception writeFailure)
            {
                throw new AggregateException(failure, writeFailure);
            }

            throw;
        }
    }

    /// <summary>
    /// A read that did not complete fails the reads; when every planned read completed, the
    /// failure came after them (plan replay, environment capture, artifacts).
    /// </summary>
    public static string FailedPhase(bool writing, bool allReadsCompleted)
    {
        if (writing)
        {
            return PerfBenchmarkSideStatus.WritesPhase;
        }

        return allReadsCompleted ? PerfBenchmarkSideStatus.PostReadPhase : PerfBenchmarkSideStatus.ReadsPhase;
    }

    /// <summary>
    /// The live steps for a side hosted by <paramref name="harness" />. The optional write
    /// hooks run right before the first and right after the last write request, outside the
    /// verification, so a smoke can measure connection reuse over exactly the write phase.
    /// </summary>
    public static PerfBenchmarkSideSteps LiveSteps(
        ApiIntegrationHarness harness,
        PerfBenchmarkSidePlan plan,
        string leasedConnectionString,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        PerfEvidenceRunSettings settings,
        PerfSlowdownInjection? injection,
        Func<Task>? beforeWrites = null,
        Func<Task>? afterWrites = null
    )
    {
        PerfBenchmarkSideWorkload workload = plan.Workload;
        PerfFixtureDefinition definition = plan.FixtureDefinition;

        return new PerfBenchmarkSideSteps(
            async () =>
            {
                bool published = PublishPrimaryOwnership(harness, leasedConnectionString);
                return new PerfBenchmarkSideFacts(
                    EffectiveConnectionSettings(plan.Provider, leasedConnectionString),
                    await EffectiveSchemaHashAsync(harness.DbConnection, plan.Provider),
                    EffectiveLogLevel(harness.Services),
                    published,
                    new PerfBenchmarkInstrumentation(
                        harness.QueryRecorder is not null,
                        DriverCommandObserver: true
                    )
                );
            },
            observer =>
                PerfBaselineRunPipeline.RunAsync(
                    harness,
                    plan.Provider,
                    openReplayConnectionAsync,
                    leasedConnectionString,
                    definition,
                    workload.DeepOffset,
                    workload.Reads.WarmupIterations,
                    workload.Reads.MeasuredIterations,
                    plan.SideDirectory,
                    plan.RunnerCommit,
                    // The environment contract, not GITHUB_ACTIONS, decides whether a host is
                    // suitable for benchmark evidence.
                    settings with
                    {
                        AllowCi = true,
                    },
                    observer,
                    // Every side of a build must run the same plans, so PostgreSQL sides get an
                    // identical physical layout (see PgsqlPerfFixtureLoaderSql.CompactionSqls).
                    plan.Provider == PerfProvider.Postgresql
                        ? () => PerfFixtureLoader.CompactPostgresqlAsync(harness.DbConnection)
                        : null
                ),
            async outcomes =>
            {
                PerfWriteTargets targets = PerfWriteTargets.Select(
                    definition.RowCount,
                    workload.Writes.WarmupIterations,
                    workload.Writes.MeasuredIterations
                );
                IReadOnlyList<string> before = await PerfWriteScenarioExecutor.VerifyAsync(
                    harness.DbConnection,
                    plan.Provider,
                    definition,
                    targets
                );
                if (plan.Provider == PerfProvider.Postgresql)
                {
                    await CheckpointPostgresqlAsync(harness.DbConnection);
                }

                if (beforeWrites is not null)
                {
                    await beforeWrites();
                }

                PerfWriteScenarioResult result = await PerfWriteScenarioExecutor.RunAsync(
                    harness,
                    plan.Provider,
                    targets,
                    outcomes
                );
                if (afterWrites is not null)
                {
                    await afterWrites();
                }

                IReadOnlyList<string> after = await PerfWriteScenarioExecutor.VerifyAsync(
                    harness.DbConnection,
                    plan.Provider,
                    definition,
                    targets
                );
                return new PerfBenchmarkWriteOutcome(result, before, after);
            },
            () => injection?.AffectedRequests ?? PerfBenchmarkScenarios.AllIds.ToDictionary(id => id, _ => 0)
        );
    }

    /// <summary>
    /// Forces a checkpoint right before the PostgreSQL write phase. A checkpoint resets the
    /// server's checkpoint_timeout timer, so no timed checkpoint can start during the few
    /// seconds of writes; a running checkpoint slows every commit, which waits on its fsyncs
    /// and WAL recycling.
    /// </summary>
    private static async Task CheckpointPostgresqlAsync(DbConnection connection)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "CHECKPOINT;";
        command.CommandTimeout = 600;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The slowdown middleware for a side, or null when the side injects nothing.
    /// </summary>
    public static PerfSlowdownInjection? InjectionFor(PerfBenchmarkSidePlan plan) =>
        plan.Injection.IsNone ? null : new PerfSlowdownInjection(plan.Injection, plan.Workload.DeepOffset);

    /// <summary>
    /// Registers the slowdown middleware ahead of the application pipeline, only when the
    /// side injects something.
    /// </summary>
    public static void RegisterInjection(IServiceCollection services, PerfSlowdownInjection? injection)
    {
        if (injection is not null)
        {
            services.AddSingleton<IStartupFilter>(injection);
        }
    }

    /// <summary>
    /// Host settings every benchmark side runs with: Serilog at Warning. The scalar
    /// <c>Serilog:MinimumLevel</c> form is required: the test host passes these settings as
    /// command-line arguments together with every parent section, so a nested
    /// <c>Serilog:MinimumLevel:Default</c> would also publish <c>Serilog:MinimumLevel</c> as an
    /// empty string, which Serilog then reads as the (invalid) level. Serilog takes the
    /// scalar from the last provider that sets either form, and these settings are last.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> HostSettings = new Dictionary<string, string>
    {
        ["Serilog:MinimumLevel"] = "Warning",
    };

    /// <summary>
    /// Publishes data-store ownership for the leased primary, as the production configuration
    /// service provider does after each load (the DMS-1565 pattern). Without it the host
    /// retires the pool when its last lease ends, so every request opens a new connection.
    /// Returns whether any reconciler received the snapshot.
    /// </summary>
    public static bool PublishPrimaryOwnership(ApiIntegrationHarness harness, string leasedConnectionString)
    {
        DataStoreOwnershipSnapshot snapshot = new(
            1,
            ImmutableArray.Create(
                new ConfiguredTargetOwner(
                    string.Empty,
                    ExternalDoublesConstants.StableDataStoreId,
                    EffectiveTargetKind.Primary,
                    leasedConnectionString
                )
            )
        );

        List<IDataStoreOwnershipReconciler> reconcilers =
        [
            .. harness.Services.GetServices<IDataStoreOwnershipReconciler>(),
        ];
        foreach (IDataStoreOwnershipReconciler reconciler in reconcilers)
        {
            reconciler.Reconcile(snapshot);
        }

        return reconcilers.Count > 0;
    }

    /// <summary>
    /// PostgreSQL: read back from a data source built by the production cache from the
    /// leased string, which rewrites the configured string in code. SQL Server: a primary
    /// uses the leased string unchanged.
    /// </summary>
    public static PerfBenchmarkConnectionSettings EffectiveConnectionSettings(
        PerfProvider provider,
        string leasedConnectionString
    )
    {
        if (provider == PerfProvider.Postgresql)
        {
            using NpgsqlDataSourceCache cache = new(NullLogger<NpgsqlDataSourceCache>.Instance);
            using NpgsqlDataSourceLease lease = cache.AcquireLease(leasedConnectionString);
            NpgsqlConnectionStringBuilder effective = new(lease.DataSource.ConnectionString);
            return new PerfBenchmarkConnectionSettings(
                effective.Pooling,
                effective.MinPoolSize,
                effective.MaxPoolSize,
                effective.NoResetOnClose,
                effective.AutoPrepareMinUsages,
                effective.MaxAutoPrepare
            );
        }

        SqlConnectionStringBuilder sqlServer = new(leasedConnectionString);
        return new PerfBenchmarkConnectionSettings(
            sqlServer.Pooling,
            sqlServer.MinPoolSize,
            sqlServer.MaxPoolSize
        );
    }

    /// <summary>
    /// The lowest level the host's DMS loggers emit, which Serilog's minimum level decides.
    /// </summary>
    public static string EffectiveLogLevel(IServiceProvider services)
    {
        ILogger logger = services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("EdFi.DataManagementService");
        LogLevel[] levels =
        [
            LogLevel.Trace,
            LogLevel.Debug,
            LogLevel.Information,
            LogLevel.Warning,
            LogLevel.Error,
            LogLevel.Critical,
        ];
        return levels.Where(logger.IsEnabled).Select(level => level.ToString()).FirstOrDefault() ?? "None";
    }

    private static async Task<string> EffectiveSchemaHashAsync(DbConnection connection, PerfProvider provider)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            provider == PerfProvider.Postgresql
                ? """SELECT "EffectiveSchemaHash" FROM "dms"."EffectiveSchema";"""
                : "SELECT [EffectiveSchemaHash] FROM [dms].[EffectiveSchema];";
        return (await command.ExecuteScalarAsync()) as string
            ?? throw new PerfObservationException("The database records no EffectiveSchemaHash.");
    }

    private static PerfBenchmarkSideResult Build(
        PerfBenchmarkSidePlan plan,
        PerfBenchmarkSideStatus status,
        PerfBenchmarkSideFacts facts,
        PerfBenchmarkReadObserver observer,
        PerfWriteScenarioResult write,
        IReadOnlyDictionary<string, int> affectedRequests,
        PerfBenchmarkReadRunReference? readRun
    ) =>
        new(
            PerfBenchmarkSideResult.CurrentSchemaVersion,
            plan.Identity.RunId,
            status,
            plan.Identity.Role,
            plan.Identity.Pair,
            plan.Identity.Position,
            plan.Profile.Profile.ProfileVersion,
            plan.Profile.Sha256,
            plan.Workload,
            observer.Counts,
            write,
            facts.EffectiveConnection,
            facts.EffectiveSchemaHash,
            facts.EffectiveLogLevel,
            facts.OwnershipPublished,
            facts.Instrumentation,
            new PerfBenchmarkInjection(plan.Injection.Factors, affectedRequests),
            readRun
        );

    /// <summary>
    /// Writes the side result and reads it back strictly: the evidence is the file.
    /// </summary>
    private static void Write(string sideDirectory, PerfBenchmarkSideResult result)
    {
        Directory.CreateDirectory(sideDirectory);
        string path = Path.Combine(sideDirectory, PerfBenchmarkSideResult.FileName);
        File.WriteAllText(path, PerfBenchmarkSideResults.Serialize(result));
        PerfBenchmarkSideResults.Read(File.ReadAllText(path));
    }
}
