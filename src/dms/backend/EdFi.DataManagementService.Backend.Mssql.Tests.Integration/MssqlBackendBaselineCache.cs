// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlBackendBaselineCacheEntry(
    string GeneratedDdlHash,
    Lazy<Task<MssqlGeneratedDdlBaselineDatabase>> Baseline
);

/// <summary>
/// Keeps one snapshot-backed generated-DDL baseline per backend fixture signature in a test process.
/// </summary>
internal static class MssqlBackendBaselineCache
{
    private static readonly ConcurrentDictionary<string, MssqlBackendBaselineCacheEntry> _cache = new(
        StringComparer.Ordinal
    );

    public static async Task<MssqlGeneratedDdlBaselineDatabase> CreateOrGetAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = 300
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var generatedDdlHash = ComputeGeneratedDdlHash(generatedDdl);

        var candidate = new MssqlBackendBaselineCacheEntry(
            generatedDdlHash,
            new(
                () =>
                    MssqlGeneratedDdlBaselineDatabase.CreateAsync(
                        fixtureSignature,
                        generatedDdl,
                        commandTimeoutSeconds
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
                MssqlGeneratedDdlBaselineDatabase baseline = await entry.Value.Baseline.Value;
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
