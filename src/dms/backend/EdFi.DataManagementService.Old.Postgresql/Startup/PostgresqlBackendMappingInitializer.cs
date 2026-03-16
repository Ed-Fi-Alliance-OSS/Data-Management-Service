// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlBackendMappingInitializer(
    PostgresqlRuntimeMappingSetAccessor runtimeMappingSetAccessor,
    ILogger<PostgresqlBackendMappingInitializer> logger
) : IBackendMappingInitializer
{
    /// <summary>
    /// Loads/compiles the PostgreSQL runtime mapping set. Instance-level database
    /// validation (fingerprint + resource key seed) is handled by the Core-level
    /// <c>ValidateStartupInstancesTask</c> (Order 310), which runs after this task
    /// and supports per-instance failure isolation.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var cacheResult = await runtimeMappingSetAccessor
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        var mappingSetKey = cacheResult.MappingSet.Key;
        var initializationMode = GetInitializationMode(cacheResult.CacheStatus);

        logger.LogInformation(
            "{InitializationMode} PostgreSQL runtime mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
            initializationMode,
            mappingSetKey.EffectiveSchemaHash,
            mappingSetKey.Dialect,
            mappingSetKey.RelationalMappingVersion
        );
    }

    private static string GetInitializationMode(MappingSetCacheStatus cacheStatus)
    {
        return cacheStatus switch
        {
            MappingSetCacheStatus.Compiled => "Compiled",
            MappingSetCacheStatus.JoinedInFlight => "Joined in-flight",
            MappingSetCacheStatus.ReusedCompleted => "Reused completed",
            _ => throw new ArgumentOutOfRangeException(nameof(cacheStatus), cacheStatus, null),
        };
    }
}
