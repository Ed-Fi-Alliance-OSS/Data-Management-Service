// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration.Mssql;

/// <summary>
/// Wraps <see cref="MssqlGeneratedDdlBaselineDatabase"/> per fixture so the same
/// snapshot-backed baseline is reused across tests within a process. Per-fixture
/// provisioning (loading the effective schema set, running the production DDL
/// pipeline, and applying the resulting DDL) happens once; per-test leases are
/// then acquired by callers via
/// <see cref="MssqlGeneratedDdlBaselineDatabase.AcquireRestoredDatabaseAsync(int?)"/>.
/// </summary>
internal static class MssqlBaselineCache
{
    private static readonly ConcurrentDictionary<
        FixtureKey,
        Lazy<Task<MssqlGeneratedDdlBaselineDatabase>>
    > _cache = new();

    /// <summary>
    /// Returns the cached baseline database for the supplied fixture, building it on
    /// first access. The fixture signature handed to the baseline is the
    /// <see cref="FixtureKey"/> value so distinct fixtures get distinct snapshots.
    /// </summary>
    public static async Task<MssqlGeneratedDdlBaselineDatabase> CreateOrGetAsync(FixtureContext fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        Lazy<Task<MssqlGeneratedDdlBaselineDatabase>> lazy = _cache.GetOrAdd(
            fixture.Key,
            key => new(() => BuildBaselineAsync(key), LazyThreadSafetyMode.ExecutionAndPublication)
        );

        try
        {
            return await lazy.Value;
        }
        catch
        {
            _cache.TryRemove(new(fixture.Key, lazy));
            throw;
        }
    }

    private static async Task<MssqlGeneratedDdlBaselineDatabase> BuildBaselineAsync(FixtureKey key)
    {
        string fixtureDirectory = FixtureRepositoryPaths.ResolveFixtureDirectory(key);
        EffectiveSchemaSet effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(
            fixtureDirectory
        );
        (_, string generatedDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Mssql,
            strict: true
        );

        return await MssqlGeneratedDdlBaselineDatabase.CreateAsync(key.ToString(), generatedDdl);
    }
}
