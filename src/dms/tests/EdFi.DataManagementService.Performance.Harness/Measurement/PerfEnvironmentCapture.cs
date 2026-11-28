// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Captures the run manifest's environment identity: server version and settings from the
/// live database, driver identity from the connection's assembly, and host facts from the
/// runtime. The machine fingerprint is a pseudonym of Environment.MachineName (a hash
/// prefix), so committed artifacts can prove two runs shared an environment without
/// publishing it — it is not a hardware identity and is meaningful only alongside the
/// recorded OS/CPU/core/memory/.NET facts.
/// </summary>
public static class PerfEnvironmentCapture
{
    /// <summary>
    /// An explicit allowlist of the settings that change what a benchmark measures. Settings
    /// are never captured by their source: an override such as <c>primary_conninfo</c> can carry
    /// credentials, and these values are written to evidence artifacts.
    /// <c>pg_settings</c> reports each setting's effective value for the session, so a change
    /// through <c>ALTER SYSTEM</c>, a <c>-c</c> flag, <c>ALTER DATABASE</c> or
    /// <c>ALTER ROLE</c> is captured too.
    /// </summary>
    private static readonly string[] _postgresqlSettingNames =
    [
        // Memory, JIT and timing (the paging reads).
        "shared_buffers",
        "work_mem",
        "jit",
        "track_io_timing",
        "max_parallel_workers_per_gather",
        "plan_cache_mode",
        // The commit and WAL path (the measured write).
        "fsync",
        "synchronous_commit",
        "full_page_writes",
        "wal_sync_method",
        "wal_level",
        "wal_compression",
        "commit_delay",
        // Checkpoint timing.
        "checkpoint_timeout",
        "max_wal_size",
        // The planner (the measured reads).
        "random_page_cost",
        "seq_page_cost",
        "effective_cache_size",
        "effective_io_concurrency",
        "enable_seqscan",
        "enable_indexscan",
        "enable_indexonlyscan",
        "enable_bitmapscan",
        "enable_hashjoin",
        "enable_mergejoin",
        "enable_nestloop",
        "enable_sort",
    ];

    /// <summary>
    /// The PostgreSQL settings captured, in the order <c>pg_settings</c> is read.
    /// </summary>
    public static IReadOnlyList<string> PostgresqlSettingNames => _postgresqlSettingNames;

    public static async Task<PerfEnvironmentIdentity> CaptureAsync(
        DbConnection connection,
        PerfProvider provider,
        string imageTag,
        string imageDigest,
        string storageNote,
        string rawConnectionString
    )
    {
        string serverVersion;
        List<PerfSetting> settings = [];
        if (provider == PerfProvider.Postgresql)
        {
            serverVersion = await ScalarStringAsync(connection, "SELECT version();");
            await using DbCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT name, setting FROM pg_settings WHERE name = ANY(@names) ORDER BY name;";
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "names";
            parameter.Value = _postgresqlSettingNames;
            command.Parameters.Add(parameter);
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                settings.Add(new PerfSetting(reader.GetString(0), reader.GetString(1)));
            }

            if (settings.Count == 0)
            {
                throw new PerfObservationException("No server settings could be captured.");
            }

            settings.AddRange(CaptureNpgsqlAutoPrepareSettings(rawConnectionString));
        }
        else
        {
            serverVersion = await ScalarStringAsync(connection, "SELECT @@VERSION;");
            settings.Add(
                new PerfSetting(
                    "edition",
                    await ScalarStringAsync(
                        connection,
                        "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128));"
                    )
                )
            );
            // The database options that change what a read or a commit costs: row versioning
            // for reads, version-store writes, and whether a commit waits for its log flush.
            await using (DbCommand options = connection.CreateCommand())
            {
                options.CommandText = """
                    SELECT CAST(is_read_committed_snapshot_on AS nvarchar(5)),
                        snapshot_isolation_state_desc,
                        delayed_durability_desc
                    FROM sys.databases
                    WHERE name = DB_NAME();
                    """;
                await using DbDataReader row = await options.ExecuteReaderAsync();
                if (!await row.ReadAsync())
                {
                    throw new PerfObservationException("The current database is not in sys.databases.");
                }

                settings.Add(new PerfSetting("is_read_committed_snapshot_on", row.GetString(0)));
                settings.Add(new PerfSetting("snapshot_isolation_state_desc", row.GetString(1)));
                settings.Add(new PerfSetting("delayed_durability_desc", row.GetString(2)));
            }

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT name, CAST(value_in_use AS nvarchar(64))
                FROM sys.configurations
                WHERE name IN ('max degree of parallelism', 'cost threshold for parallelism', 'max server memory (MB)')
                ORDER BY name;
                """;
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                settings.Add(new PerfSetting(reader.GetString(0), reader.GetString(1)));
            }
        }

        Assembly driverAssembly = connection.GetType().Assembly;
        AssemblyName driver = driverAssembly.GetName();
        string driverLabel = driver.Name ?? "driver";

        return PerfEnvironmentIdentity.Create(
            PerfServerIdentity.Create(
                serverVersion,
                imageTag,
                imageDigest,
                storageNote,
                RedactConnectionString(rawConnectionString),
                settings
            ),
            new PerfHostIdentity(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                ResolveCpuModel(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Environment.Version.ToString(),
                GCSettings.IsServerGC,
                MachineFingerprint()
            ),
            [
                new PerfSetting(driverLabel, DriverPackageVersionOf(driverAssembly)),
                new PerfSetting(driverLabel + "-assembly", driver.Version?.ToString() ?? "unknown"),
            ]
        );
    }

    /// <summary>
    /// The effective Npgsql plan-caching settings of the measured application's
    /// connections. The production NpgsqlDataSourceCache rewrites the configured
    /// connection string in code (auto-prepare above all), so the raw connection string
    /// cannot prove these values; they are read back from a data source built by that same
    /// production code path from the same leased connection string.
    /// </summary>
    public static IReadOnlyList<PerfSetting> CaptureNpgsqlAutoPrepareSettings(string leasedConnectionString)
    {
        using NpgsqlDataSourceCache cache = new(NullLogger<NpgsqlDataSourceCache>.Instance);

        // Leased, like every other consumer: the cache has no unleased way in, which is what keeps a
        // data source from outliving what the cache knows about. Released before the cache is
        // disposed, so this capture leaves nothing behind.
        using NpgsqlDataSourceLease lease = cache.AcquireLease(leasedConnectionString);
        NpgsqlConnectionStringBuilder effective = new(lease.DataSource.ConnectionString);
        return
        [
            new PerfSetting(
                "npgsql_auto_prepare_min_usages",
                effective.AutoPrepareMinUsages.ToString(CultureInfo.InvariantCulture)
            ),
            new PerfSetting(
                "npgsql_max_auto_prepare",
                effective.MaxAutoPrepare.ToString(CultureInfo.InvariantCulture)
            ),
        ];
    }

    /// <summary>
    /// Replaces every password/pwd value in the connection string with REDACTED. Only the
    /// explicitly configured keys survive, normalized by DbConnectionStringBuilder; options
    /// the application's data-source code sets on top of this string (such as Npgsql
    /// auto-prepare) are not visible here and are captured as settings instead.
    /// </summary>
    public static string RedactConnectionString(string rawConnectionString)
    {
        DbConnectionStringBuilder builder = new() { ConnectionString = rawConnectionString };
        foreach (string key in builder.Keys.Cast<string>().ToList())
        {
            string normalized = key.Trim().ToLowerInvariant();
            if (normalized is "password" or "pwd")
            {
                builder[key] = "REDACTED";
            }
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// The driver's NuGet package version, taken from the informational version attribute
    /// (with build metadata stripped). The assembly version alone is too coarse for the
    /// final gate's driver-parity check: SqlClient ships assembly version 6.0.0.0 across
    /// several package versions.
    /// </summary>
    public static string DriverPackageVersionOf(Assembly assembly) =>
        NormalizePackageVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version
        );

    public static string NormalizePackageVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+')[0].Trim();
        }

        return assemblyVersion?.ToString() ?? "unknown";
    }

    private static string MachineFingerprint() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..16];

    /// <summary>
    /// The processor identity string: PROCESSOR_IDENTIFIER on Windows, which names the
    /// family/model/stepping rather than the marketing model name, or the /proc/cpuinfo
    /// model name elsewhere.
    /// </summary>
    private static string ResolveCpuModel()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        const string cpuInfoPath = "/proc/cpuinfo";
        if (File.Exists(cpuInfoPath))
        {
            string? modelLine = File.ReadLines(cpuInfoPath)
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal));
            if (modelLine is not null)
            {
                return modelLine[(modelLine.IndexOf(':') + 1)..].Trim();
            }
        }

        throw new PerfObservationException("The CPU model could not be resolved on this host.");
    }

    private static async Task<string> ScalarStringAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return value as string
            ?? throw new PerfObservationException($"Scalar string query returned no value: {sql}");
    }
}
