// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Captures the run manifest's environment identity: server version and settings from the
/// live database, driver identity from the connection's assembly, and host facts from the
/// runtime. The machine fingerprint is a pseudonym (hash prefix of the machine name), so
/// committed artifacts can prove two runs shared an environment without publishing it.
/// </summary>
public static class PerfEnvironmentCapture
{
    private static readonly string[] _postgresqlSettingNames =
    [
        "shared_buffers",
        "work_mem",
        "jit",
        "track_io_timing",
        "max_parallel_workers_per_gather",
        "plan_cache_mode",
    ];

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
            settings.Add(
                new PerfSetting(
                    "is_read_committed_snapshot_on",
                    await ScalarStringAsync(
                        connection,
                        """
                        SELECT CAST(is_read_committed_snapshot_on AS nvarchar(5))
                        FROM sys.databases
                        WHERE name = DB_NAME();
                        """
                    )
                )
            );
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

        if (settings.Count == 0)
        {
            throw new PerfObservationException("No server settings could be captured.");
        }

        AssemblyName driver = connection.GetType().Assembly.GetName();

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
            [new PerfSetting(driver.Name ?? "driver", driver.Version?.ToString() ?? "unknown")]
        );
    }

    /// <summary>
    /// Replaces every password/pwd value in the connection string with REDACTED, preserving
    /// the rest of the shape (pooling, prepare, and timeout settings are plan-relevant).
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

    private static string MachineFingerprint() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..16];

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
