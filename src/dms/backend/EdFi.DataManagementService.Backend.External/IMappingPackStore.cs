// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Loads mapping pack payloads from an external store (filesystem, Redis, database, etc.).
/// </summary>
/// <remarks>
/// This interface is a placeholder for the pack loading contract. The return type will
/// narrow to the protobuf-generated <c>MappingPackPayload</c> type when the contracts
/// package (<c>EdFi.DataManagementService.MappingPacks.Contracts</c>) is created in DMS-968.
/// </remarks>
public interface IMappingPackStore
{
    /// <summary>
    /// Attempts to load a mapping pack payload for the given selection key.
    /// </summary>
    /// <param name="key">The mapping set selection key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The decoded pack payload, or <c>null</c> if no pack is available for the key.
    /// </returns>
    Task<MappingPackPayload?> TryLoadPayloadAsync(MappingSetKey key, CancellationToken cancellationToken);
}
