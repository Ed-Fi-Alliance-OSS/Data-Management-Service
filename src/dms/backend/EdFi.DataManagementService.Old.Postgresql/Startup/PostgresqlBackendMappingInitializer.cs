// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlBackendMappingInitializer(
    PostgresqlRuntimeMappingSetAccessor runtimeMappingSetAccessor,
    PostgresqlRuntimeInstanceMappingValidator runtimeInstanceMappingValidator,
    ILogger<PostgresqlBackendMappingInitializer> logger
) : IBackendMappingInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var cacheResult = await runtimeMappingSetAccessor
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        var mappingSetKey = cacheResult.MappingSet.Key;
        var validationSummary = await runtimeInstanceMappingValidator
            .ValidateLoadedInstancesAsync(cacheResult.MappingSet, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "{InitializationMode} PostgreSQL runtime mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}; validated {ValidatedDatabaseCount} database(s) across {InstanceCount} loaded DMS instance(s), reused {ReusedValidationCount} cached validation(s)",
            cacheResult.WasCacheHit ? "Reused cached" : "Compiled",
            mappingSetKey.EffectiveSchemaHash,
            mappingSetKey.Dialect,
            mappingSetKey.RelationalMappingVersion,
            validationSummary.ValidatedDatabaseCount,
            validationSummary.InstanceCount,
            validationSummary.ReusedValidationCount
        );
    }
}
