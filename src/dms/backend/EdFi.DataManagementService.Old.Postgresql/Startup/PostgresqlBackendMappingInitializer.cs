// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Backend.External.LogSanitizer;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlBackendMappingInitializer(
    IMappingSetProvider mappingSetProvider,
    IRuntimeMappingSetCompiler pgsqlCompiler,
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
        var key = pgsqlCompiler.GetCurrentKey();

        logger.LogInformation(
            "Initializing PostgreSQL mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, RelationalMappingVersion {RelationalMappingVersion}",
            SanitizeForLog(key.EffectiveSchemaHash),
            SanitizeForLog(key.RelationalMappingVersion)
        );

        var mappingSet = await mappingSetProvider
            .GetOrCreateAsync(key, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "PostgreSQL mapping set ready for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}",
            SanitizeForLog(mappingSet.Key.EffectiveSchemaHash),
            mappingSet.Key.Dialect
        );
    }
}
