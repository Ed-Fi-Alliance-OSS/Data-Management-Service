// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlValidatedResourceKeyMapCache
{
    private readonly ConcurrentDictionary<
        string,
        PostgresqlValidatedResourceKeyMaps
    > _mapsByConnectionString = new(StringComparer.Ordinal);

    public int Count => _mapsByConnectionString.Count;

    public bool TryGet(string connectionString, out PostgresqlValidatedResourceKeyMaps validatedMaps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (
            _mapsByConnectionString.TryGetValue(connectionString, out var cachedMaps)
            && cachedMaps is not null
        )
        {
            validatedMaps = cachedMaps;
            return true;
        }

        validatedMaps = null!;
        return false;
    }

    public PostgresqlValidatedResourceKeyMaps Set(string connectionString, MappingSet mappingSet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(mappingSet);

        var validatedMaps = new PostgresqlValidatedResourceKeyMaps(
            mappingSet.Key,
            mappingSet.ResourceKeyIdByResource,
            mappingSet.ResourceKeyById
        );

        _mapsByConnectionString[connectionString] = validatedMaps;

        return validatedMaps;
    }
}

internal sealed record PostgresqlValidatedResourceKeyMaps(
    MappingSetKey MappingSetKey,
    IReadOnlyDictionary<QualifiedResourceName, short> ResourceKeyIdByResource,
    IReadOnlyDictionary<short, ResourceKeyEntry> ResourceKeyById
);
