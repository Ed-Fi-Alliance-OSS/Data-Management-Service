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
/// Wraps <see cref="IMssqlGeneratedDdlBaselineDatabase"/> per fixture so the same
/// strategy-selected baseline is reused across tests within a process. Per-fixture
/// provisioning (loading the effective schema set, running the production DDL
/// pipeline, and applying the resulting DDL) happens once; per-test leases are
/// then acquired by callers via
/// <see cref="IMssqlGeneratedDdlBaselineDatabase.AcquireRestoredDatabaseAsync(int?)"/>.
/// </summary>
internal static class MssqlBaselineCache
{
    private static readonly ConcurrentDictionary<
        FixtureKey,
        Lazy<Task<IMssqlGeneratedDdlBaselineDatabase>>
    > _cache = new();

    /// <summary>
    /// Returns the cached baseline database for the supplied fixture, building it on
    /// first access. The fixture signature handed to the baseline is the
    /// <see cref="FixtureKey"/> value so distinct fixtures get distinct snapshots.
    /// The DDL is generated from the same materialized ApiSchema directory the DMS
    /// host loads through the bootstrap manifest so the effective schema hash
    /// matches at request time.
    /// </summary>
    public static async Task<IMssqlGeneratedDdlBaselineDatabase> CreateOrGetAsync(FixtureContext fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        Lazy<Task<IMssqlGeneratedDdlBaselineDatabase>> lazy = _cache.GetOrAdd(
            fixture.Key,
            _ => new(() => BuildBaselineAsync(fixture), LazyThreadSafetyMode.ExecutionAndPublication)
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

    private static async Task<IMssqlGeneratedDdlBaselineDatabase> BuildBaselineAsync(FixtureContext fixture)
    {
        EffectiveSchemaSet effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(
            fixture.ApiSchemaDirectory
        );
        (_, string generatedDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Mssql,
            strict: true
        );

        return await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            fixture.Key.ToString(),
            generatedDdl
        );
    }

    /// <summary>
    /// Disposes every cached baseline database and removes it from the cache.
    /// Drives the underlying template-database cleanup. Safe to call from an
    /// assembly-level teardown; subsequent <see cref="CreateOrGetAsync"/> calls
    /// will repopulate the cache.
    /// </summary>
    public static async Task DisposeAllAsync()
    {
        foreach (
            KeyValuePair<FixtureKey, Lazy<Task<IMssqlGeneratedDdlBaselineDatabase>>> entry in _cache.ToArray()
        )
        {
            if (!_cache.TryRemove(entry))
            {
                continue;
            }
            if (!entry.Value.IsValueCreated)
            {
                continue;
            }
            try
            {
                IMssqlGeneratedDdlBaselineDatabase baseline = await entry.Value.Value;
                await baseline.DisposeAsync();
            }
            catch
            {
                // Best-effort teardown; do not mask test failures.
            }
        }
    }
}
