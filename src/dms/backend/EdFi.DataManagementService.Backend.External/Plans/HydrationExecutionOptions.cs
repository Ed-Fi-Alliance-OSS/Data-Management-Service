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
)
{
    /// <summary>
    /// Selects the table the document metadata result set reads from. Every non-descriptor caller —
    /// the read paths and the write path's current-state load, which reads the same root row the write
    /// session locked — passes <c>RootTable</c>. <c>DocumentTable</c> remains the default for callers
    /// that have no resource root row to read from.
    /// </summary>
    public DocumentMetadataSource DocumentMetadataSource { get; init; } =
        DocumentMetadataSource.DocumentTable;
}

/// <summary>
/// Identifies which table a hydration batch reads the document metadata columns
/// (<c>DocumentId</c>, <c>DocumentUuid</c>, <c>ContentVersion</c>, <c>IdentityVersion</c>,
/// <c>ContentLastModifiedAt</c>, <c>IdentityLastModifiedAt</c>) from.
/// </summary>
public enum DocumentMetadataSource
{
    /// <summary>
    /// Read metadata from the authoritative <c>dms.Document</c> row.
    /// </summary>
    DocumentTable = 0,

    /// <summary>
    /// Read metadata from the resource root table's trigger-maintained mirror columns, which carry
    /// the same column names as <c>dms.Document</c>.
    /// </summary>
    RootTable = 1,
}
