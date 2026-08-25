// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

internal sealed class ThrowingMappingSetProvider(string message) : IMappingSetProvider
{
    public int GetOrCreateCount { get; private set; }

    public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken)
    {
        GetOrCreateCount++;
        throw new InvalidOperationException(message);
    }
}

internal sealed class FixedRuntimeMappingSetCompiler(SqlDialect dialect) : IRuntimeMappingSetCompiler
{
    private readonly MappingSetKey _mappingSetKey = new(
        "0000000000000000000000000000000000000000000000000000000000000000",
        dialect,
        "test"
    );

    public int GetCurrentKeyCount { get; private set; }

    public int CompileCount { get; private set; }

    public SqlDialect Dialect { get; } = dialect;

    public MappingSetKey GetCurrentKey()
    {
        GetCurrentKeyCount++;
        return _mappingSetKey;
    }

    public Task<MappingSet> CompileAsync(MappingSetKey expectedKey, CancellationToken cancellationToken)
    {
        CompileCount++;
        throw new InvalidOperationException(
            "Runtime mapping-set compiler should not compile during this test."
        );
    }
}
