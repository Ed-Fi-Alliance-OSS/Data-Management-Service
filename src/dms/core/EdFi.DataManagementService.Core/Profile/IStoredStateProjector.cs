// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// C6 interface: produces stored-side projection for update/upsert flows.
/// Stub for now — implemented by C6 story (DMS-1118).
/// </summary>
public interface IStoredStateProjector
{
    /// <summary>
    /// Projects the stored document through the writable profile, producing
    /// stored-side scope states, visible stored collection rows, and the
    /// visible stored body. Assembles the complete ProfileAppliedWriteContext.
    /// </summary>
    ProfileAppliedWriteContext ProjectStoredState(
        JsonNode? storedDocument,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        ContentTypeDefinition writeContentType,
        ProfileAppliedWriteRequest request,
        StoredSideExistenceLookupResult existenceLookupResult
    );
}
