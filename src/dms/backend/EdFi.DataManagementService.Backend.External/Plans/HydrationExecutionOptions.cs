// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Controls which optional projection work is included in a hydration batch.
/// </summary>
/// <param name="IncludeDescriptorProjection">
/// When <see langword="true"/>, append descriptor URI projection result sets.
/// Session-scoped current-state loads can disable this when they only need storage rows.
/// </param>
/// <param name="IncludeDocumentReferenceLookup">
/// When <see langword="true"/>, append the document-reference auxiliary lookup result set
/// (only if the plan carries a <c>DocumentReferenceLookup</c>). Read paths that emit
/// <c>link.rel</c>/<c>link.href</c> need this; write-path callers that load current state
/// or read back a committed write — and read-path callers that materialize in
/// <c>StoredDocument</c> mode (internal read-modify-write fetches) — can disable it because
/// the lookup result never reaches link emission for them.
/// </param>
/// <param name="UseSingleDocumentFastPath">
/// When <see langword="true"/>, single-document PostgreSQL hydration can use direct
/// <c>DocumentId</c> predicates instead of materializing a keyset table. Defaults to
/// <see langword="false"/> so callers opt into the rollout deliberately.
/// </param>
public sealed record HydrationExecutionOptions(
    bool IncludeDescriptorProjection = true,
    bool IncludeDocumentReferenceLookup = true,
    bool UseSingleDocumentFastPath = false
);
