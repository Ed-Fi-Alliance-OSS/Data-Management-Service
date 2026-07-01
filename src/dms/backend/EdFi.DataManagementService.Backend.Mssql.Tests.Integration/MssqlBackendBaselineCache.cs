// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlBackendBaselineCacheEntry(
    string GeneratedDdlHash,
    Lazy<Task<IMssqlGeneratedDdlBaselineDatabase>> Baseline
);

/// <summary>
/// Keeps one strategy-selected generated-DDL baseline per backend fixture signature in a test process.
/// </summary>
internal static class MssqlBackendBaselineCache
{
    private static readonly ConcurrentDictionary<string, MssqlBackendBaselineCacheEntry> _cache = new(
        StringComparer.Ordinal
    );

    public static async Task<IMssqlGeneratedDdlBaselineDatabase> CreateOrGetAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = 300,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var generatedDdlHash = ComputeGeneratedDdlHash(generatedDdl);

        var candidate = new MssqlBackendBaselineCacheEntry(
            generatedDdlHash,
            new(
                () =>
                    MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
                        fixtureSignature,
                        generatedDdl,
                        commandTimeoutSeconds,
                        callerMemberName,
                        callerFilePath,
                        callerLineNumber
                    ),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );

        MssqlBackendBaselineCacheEntry entry = _cache.GetOrAdd(fixtureSignature, candidate);
        if (!string.Equals(entry.GeneratedDdlHash, generatedDdlHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fixture signature '{fixtureSignature}' is already associated with different generated DDL."
            );
        }

        try
        {
            return await entry.Baseline.Value;
        }
        catch
        {
            _cache.TryRemove(new(fixtureSignature, entry));
            throw;
        }
    }

    public static async Task<IMssqlGeneratedDdlBaselineLease> AcquireLeaseAsync(
        string fixtureRelativePath,
        bool strict,
        string generatedDdl,
        int commandTimeoutSeconds = 300,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        IMssqlGeneratedDdlBaselineDatabase baseline = await CreateOrGetAsync(
            BuildFixtureSignature(fixtureRelativePath, strict),
            generatedDdl,
            commandTimeoutSeconds,
            callerMemberName,
            callerFilePath,
            callerLineNumber
        );

        return await baseline.AcquireRestoredDatabaseAsync(commandTimeoutSeconds);
    }

    public static string BuildFixtureSignature(string fixtureRelativePath, bool strict) =>
        $"{fixtureRelativePath}|strict={strict}";

    public static async Task DisposeAllAsync()
    {
        foreach (KeyValuePair<string, MssqlBackendBaselineCacheEntry> entry in _cache.ToArray())
        {
            if (!_cache.TryRemove(entry))
            {
                continue;
            }

            if (!entry.Value.Baseline.IsValueCreated)
            {
                continue;
            }

            try
            {
                IMssqlGeneratedDdlBaselineDatabase baseline = await entry.Value.Baseline.Value;
                await baseline.DisposeAsync();
            }
            catch
            {
                // Best-effort assembly teardown; do not mask test failures.
            }
        }
    }

    private static string ComputeGeneratedDdlHash(string generatedDdl) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generatedDdl)));
}
