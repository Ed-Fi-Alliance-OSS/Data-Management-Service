// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// A compiled candidate query that is selected but never hydrated. Partition boundary planning
/// consumes the candidate relation directly, so it needs the plan and its bindings rather than a
/// <see cref="PageKeysetSpec.Query" />, whose contract is built around hydrating a page of documents.
/// </summary>
/// <param name="Plan">The compiled candidate SQL plan.</param>
/// <param name="ParameterValues">Values for every parameter the plan binds.</param>
/// <param name="OrderingMode">
/// The anchor this relation was compiled against, and therefore the column it projects. Carried on the
/// plan rather than supplied again by whoever consumes it: partition-window compilation names this
/// column in its ranking, sizing, and boundary projection, and a second copy of the anchor could
/// disagree with the one the relation was actually compiled with.
/// </param>
internal sealed record CandidateQueryPlan(
    PageDocumentIdSqlPlan Plan,
    IReadOnlyDictionary<string, object?> ParameterValues,
    PageOrderingMode OrderingMode
);

/// <summary>
/// The candidate mode chosen for a plan, together with the parameter values that mode binds.
/// </summary>
/// <param name="Mode">The candidate selection mode passed to the shared SQL compiler.</param>
/// <param name="ParameterValues">Values for the mode-owned parameters, in canonical order.</param>
/// <param name="OwnedParameterNames">
/// The parameter names this mode owns, reserved by filter-name allocation. Derived from the mode
/// itself through <see cref="PageCandidateModeParameters.OwnedNames" />, which is the same derivation
/// the SQL compiler validates and emits against, so the reserved set cannot drift from the emitted
/// set. Only the active mode's own names are reserved: reserving another mode's names would suffix a
/// filter parameter that does not actually collide with anything this query emits, which would change
/// the SQL of a mode that has no stake in the name.
/// </param>
internal readonly record struct PlannedCandidateMode(
    PageCandidateMode Mode,
    IReadOnlyList<KeyValuePair<string, object?>> ParameterValues,
    IReadOnlyList<string> OwnedParameterNames
)
{
    /// <summary>
    /// The anchor <see cref="Mode" /> carries, so a planner can stamp it onto the compiled plan without
    /// re-inspecting which candidate mode it happens to be holding.
    /// </summary>
    /// <remarks>
    /// Read off the mode rather than stored beside it. Every candidate mode already names its own
    /// anchor, and the compiler resolves ordering, bounds, and projection through the same reader, so a
    /// stored second copy would be a value that could disagree with the mode the SQL is compiled from.
    /// </remarks>
    public PageOrderingMode OrderingMode => PageDocumentIdSqlCompiler.ResolveOrderingMode(Mode);
}

/// <summary>
/// Translates the Core paging choice into the backend candidate mode shared by the regular-resource
/// and descriptor page keyset planners, so the two planners cannot drift in mode selection, parameter
/// names, or bound values.
/// </summary>
internal static class PageCandidateModePlanning
{
    /// <summary>
    /// Builds the candidate mode and bound values for a live collection paging choice.
    /// </summary>
    /// <param name="paging">The Core paging choice.</param>
    /// <param name="orderingMode">
    /// The page-selection ordering key Core resolved, applied to every paging choice. A cursor page
    /// takes it too: the bounds it seeks on are expressed in the anchor, and the token it hands back
    /// carries that same anchor, so discarding it here would bound a page on one column and continue it
    /// from another.
    /// </param>
    public static PlannedCandidateMode ForPaging(CollectionPaging paging, PageOrderingMode orderingMode)
    {
        ArgumentNullException.ThrowIfNull(paging);

        return paging switch
        {
            CollectionPaging.Traditional traditional => ForTraditional(traditional, orderingMode),
            CollectionPaging.Cursor cursor => ForCursor(cursor, orderingMode),
            _ => throw new ArgumentOutOfRangeException(
                nameof(paging),
                paging.GetType().Name,
                "Unsupported collection paging mode."
            ),
        };
    }

    /// <summary>
    /// The unpaged candidate mode instance, and the single source of the parameter names that mode
    /// owns. Candidate planning reserves those names against filter collisions and partition-window
    /// compilation emits and binds them, so both read this one instance: a second construction site
    /// could reserve names the emitted SQL never uses.
    /// </summary>
    /// <remarks>
    /// Carries the default <c>DocumentId</c> anchor. Anchoring is per request, so a plan takes its
    /// anchor through <see cref="ForUnpagedCandidates" />, which copies this instance rather than
    /// building a new one — the names stay tied to this declaration while only the anchor varies.
    /// </remarks>
    public static PageCandidateMode.UnpagedCandidates UnpagedCandidatesMode { get; } = new();

    /// <summary>
    /// Builds the unpaged candidate mode for the supplied anchor. It binds no values: its partition
    /// parameter names are reserved against filter collisions, and partition-window SQL binds them when
    /// it emits them.
    /// </summary>
    /// <param name="orderingMode">
    /// The anchor the consuming partition-window SQL will rank and cut boundaries on. Required rather
    /// than defaulted, because boundaries cut on a different key than the page a client replays them as
    /// would overlap and leave rows in no partition.
    /// </param>
    public static PlannedCandidateMode ForUnpagedCandidates(PageOrderingMode orderingMode)
    {
        return Plan(UnpagedCandidatesModeFor(orderingMode), []);
    }

    /// <summary>
    /// The unpaged candidate mode carrying the supplied anchor. The single site that re-anchors
    /// <see cref="UnpagedCandidatesMode" />, so the mode a candidate relation is compiled with and the
    /// mode the partition-boundary statement is compiled with are the same construction.
    /// </summary>
    public static PageCandidateMode.UnpagedCandidates UnpagedCandidatesModeFor(PageOrderingMode orderingMode)
    {
        return UnpagedCandidatesMode with { OrderingMode = orderingMode };
    }

    private static PlannedCandidateMode ForTraditional(
        CollectionPaging.Traditional traditional,
        PageOrderingMode orderingMode
    )
    {
        var mode = new PageCandidateMode.Traditional(
            IncludeTotalCountSql: traditional.Parameters.TotalCount,
            OrderingMode: orderingMode
        );

        return Plan(
            mode,
            [
                new(mode.OffsetParameterName, (long)(traditional.Parameters.Offset ?? 0)),
                new(
                    mode.LimitParameterName,
                    (long)(traditional.Parameters.Limit ?? traditional.Parameters.MaximumPageSize)
                ),
            ]
        );
    }

    private static PlannedCandidateMode ForCursor(
        CollectionPaging.Cursor cursor,
        PageOrderingMode orderingMode
    )
    {
        var mode = new PageCandidateMode.Cursor(OrderingMode: orderingMode);

        return Plan(
            mode,
            [
                new(mode.InclusiveMinimumParameterName, cursor.Range.InclusiveMinimum),
                new(mode.InclusiveMaximumParameterName, cursor.Range.InclusiveMaximum),
                new(mode.PageSizeParameterName, (long)cursor.PageSize.Value),
            ]
        );
    }

    /// <summary>
    /// Pairs a candidate mode with its bound values and the names it reserves. Both the value keys and
    /// the reserved names come from the mode instance, so neither can name a parameter the compiled SQL
    /// will not emit.
    /// </summary>
    private static PlannedCandidateMode Plan(
        PageCandidateMode mode,
        IReadOnlyList<KeyValuePair<string, object?>> parameterValues
    )
    {
        return new PlannedCandidateMode(mode, parameterValues, PageCandidateModeParameters.OwnedNames(mode));
    }
}
