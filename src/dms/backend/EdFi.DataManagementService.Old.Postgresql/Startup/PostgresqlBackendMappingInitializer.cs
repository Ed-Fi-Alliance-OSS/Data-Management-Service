// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlBackendMappingInitializer(
    PostgresqlRuntimeMappingSetAccessor runtimeMappingSetAccessor,
    ILogger<PostgresqlBackendMappingInitializer> logger
) : IBackendMappingInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var cacheResult = await runtimeMappingSetAccessor
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        var mappingSetKey = cacheResult.MappingSet.Key;

        logger.LogInformation(
            "{InitializationMode} PostgreSQL runtime mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
            cacheResult.WasCacheHit ? "Reused cached" : "Compiled",
            mappingSetKey.EffectiveSchemaHash,
            mappingSetKey.Dialect,
            mappingSetKey.RelationalMappingVersion
        );
    }
}
