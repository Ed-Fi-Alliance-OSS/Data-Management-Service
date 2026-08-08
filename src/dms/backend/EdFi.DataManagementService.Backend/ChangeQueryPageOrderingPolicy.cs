// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Resolves page-selection ordering for GET-many queries from the request's change-version filter
/// shape (DMS-1298). Max-bearing windows (min+max or max-only) order by <c>ContentVersion</c> so
/// the planner can seek the change-version index instead of scanning <c>DocumentId</c> order and
/// rejecting most of a large table. Min-only windows keep <c>DocumentId</c> ordering: against live
/// data an update moves a row later within a still-open window, so ContentVersion ordering would
/// let offset paging return it twice while its departure shifts offsets and skips another row.
/// </summary>
/// <remarks>
/// Consulted only by traditional limit/offset page selection. Cursor and partition planning
/// (DMS-1348) always anchor on <c>DocumentId</c> and must not call this policy. Snapshot data
/// sources get their own explicit entry point when snapshot support lands; do not widen
/// <see cref="ResolveForLiveQuery"/> to cover them.
/// </remarks>
public sealed class ChangeQueryPageOrderingPolicy(bool useLegacyDocumentIdOrdering)
{
    /// <summary>
    /// The default policy: kill switch disabled, conditional ordering active.
    /// </summary>
    public static ChangeQueryPageOrderingPolicy Default { get; } = new(useLegacyDocumentIdOrdering: false);

    /// <summary>
    /// Resolves the page-selection ordering for a query against live (mutable) data.
    /// </summary>
    /// <param name="changeVersionRange">The validated change-version window, if any.</param>
    /// <returns>The ordering mode page selection must use.</returns>
    public PageOrderingMode ResolveForLiveQuery(ChangeVersionRange? changeVersionRange)
    {
        if (useLegacyDocumentIdOrdering)
        {
            return PageOrderingMode.DocumentId;
        }

        return changeVersionRange?.MaxChangeVersion is not null
            ? PageOrderingMode.ContentVersion
            : PageOrderingMode.DocumentId;
    }
}
