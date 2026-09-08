// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Executes a compiled <see cref="ResourceReadPlan"/> against the database, returning
/// structured hydrated row data for a page of documents.
/// </summary>
/// <remarks>
/// Implementations manage their own connection lifecycle. Callers provide the compiled
/// read plan (obtained from <see cref="MappingSet.ReadPlansByResource"/>) and a keyset
/// specification describing the page to hydrate.
/// </remarks>
public interface IDocumentHydrator
{
    /// <summary>
    /// Hydrates a page of documents by executing the compiled read plan's multi-result
    /// SQL batch against the database.
    /// </summary>
    /// <param name="plan">The compiled resource read plan.</param>
    /// <param name="keyset">The page keyset specification (single document or query page).</param>
    /// <param name="executionOptions">
    /// Controls which optional projections (descriptor URIs, document-reference auxiliary
    /// lookup) are included in the hydration batch. Callers that discard either result set
    /// (e.g. StoredDocument-mode GETs that do not emit <c>link</c>) opt out so the database
    /// does not run the corresponding SQL.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The hydrated page containing document metadata and per-table row data.</returns>
    Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    );
}
