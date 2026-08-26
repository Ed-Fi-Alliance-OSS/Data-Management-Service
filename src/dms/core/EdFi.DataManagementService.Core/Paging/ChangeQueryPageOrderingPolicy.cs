// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Paging;

/// <summary>
/// Resolves page-selection ordering for GET-many queries from the request's change-version filter
/// shape (DMS-1298). Max-bearing windows (min+max or max-only) order by <c>ContentVersion</c> so
/// the planner can seek the change-version index instead of scanning <c>DocumentId</c> order and
/// rejecting most of a large table. Min-only windows keep <c>DocumentId</c> ordering: against live
/// data an update moves a row later within a still-open window, so ContentVersion ordering would
/// let offset paging return it twice while its departure shifts offsets and skips another row.
/// </summary>
/// <remarks>
/// What makes a max-bearing window safe is that an update pushes the row past the maximum and out of
/// the window, so it leaves rather than moving later within it. That is a property of the ceiling,
/// not of the parameter: it holds while the maximum is at or below the current change version, which
/// is what a client reading it from <c>/availableChangeVersions</c> supplies. A maximum above the
/// current change version is an open-ended window wearing a ceiling — the sequence has not reached it
/// yet, so an update lands inside the window and a walk can return that row twice, which is the
/// min-only hazard reached through a max-bearing request.
/// <para>
/// The magnitude is deliberately not checked. Comparing it against the sequence would cost a
/// <c>GetMaxChangeVersion</c> read on every live collection read, to detect a client that has already
/// left the recommended workflow, and the weaker guarantee such a client gets is the one its own
/// input describes. Resolution therefore turns on presence alone.
/// </para>
/// </remarks>
/// <remarks>
/// Lives in Core because the resolved ordering is the page anchor, and the anchor is needed on both
/// sides of the request: Core stamps it on the outgoing continuation token and checks an incoming
/// token's marker against it, and the backend compiles page-selection SQL against the column it
/// names. Resolving it in both places would be two implementations of one rule, so Core resolves it
/// once and carries it down on the request.
/// <para>
/// <c>internal</c> deliberately: the two paging middlewares are the only callers, and the backend
/// reads the resolved mode off the request rather than deriving its own. A backend-visible resolver
/// would be a second place for that one rule to live, and a page selected under one ordering whose
/// token claims another is a walk that skips rows. Snapshot data sources get their own explicit entry
/// point when snapshot support lands; do not widen <see cref="ResolveForLiveQuery"/> to cover them.
/// </para>
/// </remarks>
internal sealed class ChangeQueryPageOrderingPolicy(bool useLegacyDocumentIdOrdering)
{
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
