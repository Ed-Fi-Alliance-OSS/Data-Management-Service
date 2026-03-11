// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlBackendMappingInitializer(
    PostgresqlRuntimeMappingSetAccessor runtimeMappingSetAccessor,
    PostgresqlRuntimeInstanceMappingValidator runtimeInstanceMappingValidator,
    IOptions<AppSettings> appSettings,
    ILogger<PostgresqlBackendMappingInitializer> logger
) : IBackendMappingInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var cacheResult = await runtimeMappingSetAccessor
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        var mappingSetKey = cacheResult.MappingSet.Key;
        var initializationMode = cacheResult.WasCacheHit ? "Reused cached" : "Compiled";

        if (!appSettings.Value.ValidateProvisionedMappingsOnStartup)
        {
            logger.LogWarning(
                "{InitializationMode} PostgreSQL runtime mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}; temporary startup validation bypass is active because AppSettings.ValidateProvisionedMappingsOnStartup is false",
                initializationMode,
                mappingSetKey.EffectiveSchemaHash,
                mappingSetKey.Dialect,
                mappingSetKey.RelationalMappingVersion
            );

            return;
        }

        var validationSummary = await runtimeInstanceMappingValidator
            .ValidateLoadedInstancesAsync(cacheResult.MappingSet, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "{InitializationMode} PostgreSQL runtime mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}; validated {ValidatedDatabaseCount} database(s) across {InstanceCount} loaded DMS instance(s), reused {ReusedValidationCount} cached validation(s)",
            initializationMode,
            mappingSetKey.EffectiveSchemaHash,
            mappingSetKey.Dialect,
            mappingSetKey.RelationalMappingVersion,
            validationSummary.ValidatedDatabaseCount,
            validationSummary.InstanceCount,
            validationSummary.ReusedValidationCount
        );
    }
}
