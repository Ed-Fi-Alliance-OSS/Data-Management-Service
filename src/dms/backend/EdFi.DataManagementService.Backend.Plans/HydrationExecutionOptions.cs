// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

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
/// or read back a committed write can disable it because the lookup result never reaches
/// link emission for them.
/// </param>
public readonly record struct HydrationExecutionOptions(
    bool IncludeDescriptorProjection = true,
    bool IncludeDocumentReferenceLookup = true
);
