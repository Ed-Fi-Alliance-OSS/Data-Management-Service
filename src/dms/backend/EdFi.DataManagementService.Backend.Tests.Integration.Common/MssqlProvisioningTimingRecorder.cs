// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

internal static class MssqlProvisioningTimingRecorder
{
    public const string DirectLeaseStrategy = "direct";

    private const string TimingsPathVariable = "MSSQL_FIXTURE_TIMINGS_PATH";
    private const string ShardVariable = "MSSQL_TEST_SHARD";
    private static readonly object _lock = new();
    private const string Header =
        "TimestampUtc,Outcome,DurationSeconds,DatabaseName,CommandTimeoutSeconds,FixtureSignature,GeneratedDdlHash,Phase,LeaseStrategy,Shard,TestWorkerId,CallerMemberName,CallerFilePath,CallerLineNumber,IsDiagnostic,Detail,BatchOrdinal,BatchCount,BatchHash";

    public static string ComputeGeneratedDdlHash(string generatedDdl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generatedDdl)));
    }

    public static void Record(
        string outcome,
        TimeSpan duration,
        string databaseName,
        int commandTimeoutSeconds,
        string fixtureSignature,
        string generatedDdlHash,
        string phase,
        string leaseStrategy,
        string callerMemberName,
        string callerFilePath,
        int callerLineNumber,
        bool isDiagnostic = false,
        string detail = "",
        int? batchOrdinal = null,
        int? batchCount = null,
        string batchHash = ""
    )
    {
        var timingsPath = Environment.GetEnvironmentVariable(TimingsPathVariable);
        if (string.IsNullOrWhiteSpace(timingsPath))
        {
            return;
        }

        RecordToPath(
            timingsPath,
            Console.Error,
            outcome,
            duration,
            databaseName,
            commandTimeoutSeconds,
            fixtureSignature,
            generatedDdlHash,
            phase,
            leaseStrategy,
            callerMemberName,
            callerFilePath,
            callerLineNumber,
            isDiagnostic,
            detail,
            batchOrdinal,
            batchCount,
            batchHash
        );
    }

    public static void RecordToPath(
        string timingsPath,
        TextWriter warningWriter,
        string outcome,
        TimeSpan duration,
        string databaseName,
        int commandTimeoutSeconds,
        string fixtureSignature,
        string generatedDdlHash,
        string phase,
        string leaseStrategy,
        string callerMemberName,
        string callerFilePath,
        int callerLineNumber,
        bool isDiagnostic = false,
        string detail = "",
        int? batchOrdinal = null,
        int? batchCount = null,
        string batchHash = ""
    )
    {
        if (string.IsNullOrWhiteSpace(timingsPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(timingsPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string[] fields =
            [
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                outcome,
                duration.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
                databaseName,
                commandTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                fixtureSignature,
                generatedDdlHash,
                phase,
                leaseStrategy,
                ResolveShard(timingsPath),
                ResolveTestWorkerId(),
                callerMemberName,
                callerFilePath,
                callerLineNumber.ToString(CultureInfo.InvariantCulture),
                isDiagnostic ? "true" : "false",
                detail,
                batchOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "",
                batchCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                batchHash,
            ];

            lock (_lock)
            {
                var writeHeader = !File.Exists(fullPath);
                using var writer = new StreamWriter(fullPath, append: true, Encoding.UTF8);
                if (writeHeader)
                {
                    writer.WriteLine(Header);
                }
                writer.WriteLine(string.Join(",", fields.Select(EscapeCsv)));
            }
        }
        catch (Exception exception)
        {
            TryWarn(warningWriter, $"MSSQL fixture timing output unavailable ({exception.GetType().Name}).");
        }
    }

    private static void TryWarn(TextWriter writer, string warning)
    {
        try
        {
            writer.WriteLine(warning);
        }
        catch
        { /* Timing output and its warning are both best-effort. */
        }
    }

    private static string ResolveShard(string timingsPath)
    {
        var shard = Environment.GetEnvironmentVariable(ShardVariable);
        if (!string.IsNullOrWhiteSpace(shard))
        {
            return shard;
        }
        var match = Regex.Match(timingsPath, @"mssql-shard-(?<shard>[^\\/]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["shard"].Value;
        }
        return timingsPath.Contains("mssql-api", StringComparison.OrdinalIgnoreCase) ? "api" : "";
    }

    private static string ResolveTestWorkerId()
    {
        foreach (var variableName in new[] { "NUNIT_WORKER_ID", "TEST_WORKER_ID" })
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        try
        {
            var contextType = Type.GetType("NUnit.Framework.TestContext, NUnit.Framework");
            var current = contextType
                ?.GetProperty("CurrentContext", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            return current
                    ?.GetType()
                    .GetProperty("WorkerId", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(current)
                    ?.ToString()
                ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
