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
internal sealed record CandidateQueryPlan(
    PageDocumentIdSqlPlan Plan,
    IReadOnlyDictionary<string, object?> ParameterValues
);

/// <summary>
/// The candidate mode chosen for a plan, together with the parameter values that mode binds.
/// </summary>
/// <param name="Mode">The candidate selection mode passed to the shared SQL compiler.</param>
/// <param name="ParameterValues">Values for the mode-owned parameters, in canonical order.</param>
/// <param name="OwnedParameterNames">
/// The parameter names this mode owns, reserved by filter-name allocation. Only the active mode's own
/// names are reserved: reserving another mode's names would suffix a filter parameter that does not
/// actually collide with anything this query emits, which would change the SQL of a mode that has no
/// stake in the name.
/// </param>
internal readonly record struct PlannedCandidateMode(
    PageCandidateMode Mode,
    IReadOnlyList<KeyValuePair<string, object?>> ParameterValues,
    IReadOnlyList<string> OwnedParameterNames
);

/// <summary>
/// Translates the Core paging choice into the backend candidate mode shared by the regular-resource
/// and descriptor page keyset planners, so the two planners cannot drift in mode selection, parameter
/// names, or bound values.
/// </summary>
internal static class PageCandidateModePlanning
{
    private static readonly string[] _traditionalOwnedParameterNames =
    [
        PageCandidateParameterNames.Offset,
        PageCandidateParameterNames.Limit,
    ];

    private static readonly string[] _cursorOwnedParameterNames =
    [
        PageCandidateParameterNames.CursorInclusiveMinimum,
        PageCandidateParameterNames.CursorInclusiveMaximum,
        PageCandidateParameterNames.PageSize,
    ];

    private static readonly string[] _unpagedCandidatesOwnedParameterNames =
    [
        PageCandidateParameterNames.PartitionCount,
        PageCandidateParameterNames.MinimumPartitionSize,
    ];

    /// <summary>
    /// Builds the candidate mode and bound values for a live collection paging choice.
    /// </summary>
    /// <param name="paging">The Core paging choice.</param>
    /// <param name="orderingMode">
    /// The page-selection ordering key. Applies to traditional paging only; a cursor page is always
    /// ordered by <c>DocumentId</c> because its continuation token is anchored on that key.
    /// </param>
    public static PlannedCandidateMode ForPaging(CollectionPaging paging, PageOrderingMode orderingMode)
    {
        ArgumentNullException.ThrowIfNull(paging);

        return paging switch
        {
            CollectionPaging.Traditional traditional => new PlannedCandidateMode(
                new PageCandidateMode.Traditional(
                    PageCandidateParameterNames.Offset,
                    PageCandidateParameterNames.Limit,
                    traditional.Parameters.TotalCount,
                    orderingMode
                ),
                [
                    new KeyValuePair<string, object?>(
                        PageCandidateParameterNames.Offset,
                        (long)(traditional.Parameters.Offset ?? 0)
                    ),
                    new KeyValuePair<string, object?>(
                        PageCandidateParameterNames.Limit,
                        (long)(traditional.Parameters.Limit ?? traditional.Parameters.MaximumPageSize)
                    ),
                ],
                _traditionalOwnedParameterNames
            ),
            CollectionPaging.Cursor cursor => new PlannedCandidateMode(
                new PageCandidateMode.Cursor(),
                [
                    new KeyValuePair<string, object?>(
                        PageCandidateParameterNames.CursorInclusiveMinimum,
                        cursor.Range.InclusiveMinimum
                    ),
                    new KeyValuePair<string, object?>(
                        PageCandidateParameterNames.CursorInclusiveMaximum,
                        cursor.Range.InclusiveMaximum
                    ),
                    new KeyValuePair<string, object?>(
                        PageCandidateParameterNames.PageSize,
                        (long)cursor.PageSize.Value
                    ),
                ],
                _cursorOwnedParameterNames
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(paging),
                paging.GetType().Name,
                "Unsupported collection paging mode."
            ),
        };
    }

    /// <summary>
    /// Builds the unpaged candidate mode. It binds no values: its partition parameter names are
    /// reserved against filter collisions, and partition-window SQL binds them when it emits them.
    /// </summary>
    public static PlannedCandidateMode ForUnpagedCandidates()
    {
        return new PlannedCandidateMode(
            new PageCandidateMode.UnpagedCandidates(),
            [],
            _unpagedCandidatesOwnedParameterNames
        );
    }
}
