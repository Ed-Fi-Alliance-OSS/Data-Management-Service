// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// No-op mapping pack store that always returns null (no pack available).
/// Placeholder until DMS-968 delivers the real file-based pack store.
/// </summary>
public sealed class NoOpMappingPackStore : IMappingPackStore
{
    /// <inheritdoc />
    public Task<MappingPackPayload?> TryLoadPayloadAsync(
        MappingSetKey key,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<MappingPackPayload?>(null);
    }
}
