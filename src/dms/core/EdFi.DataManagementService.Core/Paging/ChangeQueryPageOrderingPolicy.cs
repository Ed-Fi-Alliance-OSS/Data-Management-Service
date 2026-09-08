// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Paging;

/// <summary>
/// Resolves page-selection ordering for GET-many queries from the request's change-version filter
/// shape and the kind of data source serving it. Max-bearing windows (min+max or max-only) order by
/// <c>ContentVersion</c> so the planner can seek the change-version index instead of scanning
/// <c>DocumentId</c> order and rejecting most of a large table (DMS-1298). Min-only windows keep
/// <c>DocumentId</c> ordering <em>against live data</em>: an update moves a row later within a
/// still-open window, so ContentVersion ordering would let offset paging return it twice while its
/// departure shifts offsets and skips another row. Nothing moves in a frozen snapshot, so there a
/// min-only window orders by <c>ContentVersion</c> as well and the planner fix reaches every
/// windowed shape (DMS-1396). Callers resolve through <see cref="ResolveFor"/>, which picks the entry
/// point from the effective data-store target; the two entry points stay target-blind, so each rule
/// can be read and tested on its own. Every rule below is conditional on the legacy-ordering switch
/// this type is constructed with: when <c>useLegacyDocumentIdOrdering</c> is set, both entry points
/// return <c>DocumentId</c> for every window shape on every data store, and none of the rest applies.
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
/// token claims another is a walk that skips rows. Snapshot data sources resolve through their own
/// entry point, <see cref="ResolveForSnapshotQuery"/>, rather than through a widened live rule: what
/// qualifies a source for it is being frozen for the life of the walk, which is what removes the
/// min-only hazard. A read replica does not qualify — it keeps applying changes, so a row can still
/// move later within an open window there — and neither does anything else short of frozen.
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

    /// <summary>
    /// Resolves the page-selection ordering for a query against a frozen snapshot.
    /// </summary>
    /// <remarks>
    /// Every windowed shape resolves <c>ContentVersion</c>, min-only included. Nothing moves in a
    /// frozen source, so the duplicate-and-skip hazard that keeps live min-only windows on
    /// <c>DocumentId</c> cannot occur, while the planner pathology still can: that one is a property
    /// of the data distribution, which the copy preserves. Min-only is also the natural shape here,
    /// the newest version in the copy being the implicit maximum.
    /// <para>
    /// An unfiltered read keeps <c>DocumentId</c>. With no window predicate there is no pathology to
    /// fix and nothing to gain, and routing a request to a snapshot must not by itself change the
    /// order a collection is walked in.
    /// </para>
    /// <para>
    /// Resolution reads the parsed window, not the parameters that produced it, so a bound that was
    /// blank or failed to parse counts as absent — the same rule
    /// <see cref="ResolveForLiveQuery"/> follows, and the reason a walk is never anchored on a bound
    /// the request was rejected for.
    /// </para>
    /// </remarks>
    /// <param name="changeVersionRange">The validated change-version window, if any.</param>
    /// <returns>The ordering mode page selection must use.</returns>
    public PageOrderingMode ResolveForSnapshotQuery(ChangeVersionRange? changeVersionRange)
    {
        if (useLegacyDocumentIdOrdering)
        {
            return PageOrderingMode.DocumentId;
        }

        return changeVersionRange is { MinChangeVersion: not null } or { MaxChangeVersion: not null }
            ? PageOrderingMode.ContentVersion
            : PageOrderingMode.DocumentId;
    }

    /// <summary>
    /// Resolves the page-selection ordering for a request from its window and the kind of data store
    /// serving it, by picking the entry point that kind qualifies for.
    /// </summary>
    /// <remarks>
    /// What qualifies a source for the snapshot rule is being frozen for the life of the walk, which is
    /// what removes the min-only hazard. Only <see cref="EffectiveTargetKind.Snapshot"/> is frozen: a
    /// read replica keeps applying changes, so a row can still move later within an open window there,
    /// and it takes the live rule along with the primary. Anything short of frozen does the same.
    /// <para>
    /// The dispatch lives here rather than at each call site so that the two paging middlewares cannot
    /// come to disagree about which sources are frozen — a boundary set cut under one rule and a page
    /// selected under the other is a walk whose own tokens its follow-up requests reject. The entry
    /// points stay target-blind and independently callable, so what a given source resolves is still
    /// testable without constructing one.
    /// </para>
    /// </remarks>
    /// <param name="changeVersionRange">The validated change-version window, if any.</param>
    /// <param name="targetKind">The kind of data store this request resolved to.</param>
    /// <returns>The ordering mode page selection must use.</returns>
    public PageOrderingMode ResolveFor(
        ChangeVersionRange? changeVersionRange,
        EffectiveTargetKind targetKind
    ) =>
        targetKind is EffectiveTargetKind.Snapshot
            ? ResolveForSnapshotQuery(changeVersionRange)
            : ResolveForLiveQuery(changeVersionRange);
}
