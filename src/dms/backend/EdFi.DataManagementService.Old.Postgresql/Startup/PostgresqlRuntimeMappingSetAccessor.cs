// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlRuntimeMappingSetAccessor(
    PostgresqlRuntimeMappingSetCompiler runtimeMappingSetCompiler,
    MappingSetCache mappingSetCache
)
{
    public Task<MappingSetCacheResult> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var key = runtimeMappingSetCompiler.GetCurrentKey();
        return mappingSetCache.GetOrCreateWithCacheStatusAsync(key, cancellationToken);
    }
}
