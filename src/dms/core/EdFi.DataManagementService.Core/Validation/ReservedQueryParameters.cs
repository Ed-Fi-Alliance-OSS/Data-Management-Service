// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.Paging;

namespace EdFi.DataManagementService.Core.Validation;

/// <summary>
/// The operations a query parameter name can be reserved on.
/// </summary>
/// <remarks>
/// A single parameter is reserved on a set of operations, which is why this is a flag set. The catalog
/// queries below take one operation rather than a set, because the question they answer - which names
/// does this operation remove from filter matching - is asked by one operation at a time.
/// </remarks>
[Flags]
public enum ReservedQueryParameterOperations
{
    /// <summary>Reserved nowhere. Never the value of a catalog entry; present so the flag set has a zero.</summary>
    None = 0,

    /// <summary>The resource and descriptor collection GET-many operations.</summary>
    CollectionGet = 1,

    /// <summary>The <c>/partitions</c> operation.</summary>
    Partitions = 2,

    /// <summary>The Change Query <c>/deletes</c> and <c>/keyChanges</c> operations.</summary>
    ChangeQueries = 4,
}

/// <summary>
/// How a reserved name is compared against a supplied query key at request time.
/// </summary>
/// <remarks>
/// Recorded per name rather than assumed, because the two relations coexist in the served pipeline. The
/// paging, cursor, and partition-count names are canonicalized to their published spelling at the HTTP
/// boundary and then matched ordinally, while the change-version names are looked up
/// case-insensitively in Core. Collision detection does not use this value - it always compares
/// case-insensitively, because a query field is matched against a supplied key case-insensitively and
/// is therefore just as unfilterable whichever case it is declared in.
/// </remarks>
public enum ReservedQueryParameterMatching
{
    /// <summary>Compared with <see cref="StringComparer.Ordinal" /> against the canonical spelling.</summary>
    Ordinal,

    /// <summary>Compared with <see cref="StringComparer.OrdinalIgnoreCase" />.</summary>
    OrdinalIgnoreCase,
}

/// <summary>
/// One query parameter name DMS consumes before resource-filter matching runs.
/// </summary>
/// <param name="Name">The canonical published spelling.</param>
/// <param name="ReservedOn">Every operation that removes this name from filter matching.</param>
/// <param name="RequestMatching">How the operation compares a supplied key against <paramref name="Name" />.</param>
/// <param name="Purpose">
/// What the operation does with the name, phrased to complete the sentence "DMS reserves '{Name}' as
/// the {Purpose}". Carried so a diagnostic can tell a schema author what the name is taken for rather
/// than only that it is taken.
/// </param>
public sealed record ReservedQueryParameter(
    string Name,
    ReservedQueryParameterOperations ReservedOn,
    ReservedQueryParameterMatching RequestMatching,
    string Purpose
);

/// <summary>
/// The single declaration of every query parameter name DMS consumes as a control parameter, and
/// therefore of every name a resource query field cannot use.
/// </summary>
/// <remarks>
/// Before this catalog the set was assembled from four independent arrays in Core plus a fifth copy at
/// the HTTP boundary, so a name could be reserved in one of them without anyone asking whether a
/// resource might declare a query field of that name. Everything that reserves a name reads it from
/// here, which is what makes the question unavoidable: a name absent from this catalog is reserved
/// nowhere, and a name present in it is refused at ApiSchema load.
/// </remarks>
/// <remarks>
/// The spellings are not re-typed here. Each is read from the validator that owns it, because those
/// same constants appear in served error text and in the published OpenAPI parameter components;
/// spelling one of them twice would let the name a request is validated against drift from the name a
/// schema is refused for.
/// </remarks>
public static class ReservedQueryParameters
{
    /// <summary>
    /// Every reserved name, in the canonical order diagnostics report them: the traditional paging
    /// controls, the cursor controls, the change-version window, then the partition count.
    /// </summary>
    /// <remarks>
    /// Declared first because the per-operation projections below are computed from it, and same-type
    /// static field initializers run in textual order.
    /// </remarks>
    private static readonly ImmutableArray<ReservedQueryParameter> _all =
    [
        new(
            CursorRequestValidator.LimitParameter,
            ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries,
            ReservedQueryParameterMatching.Ordinal,
            "traditional paging page size"
        ),
        new(
            CursorRequestValidator.OffsetParameter,
            ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries,
            ReservedQueryParameterMatching.Ordinal,
            "traditional paging start position"
        ),
        new(
            CursorRequestValidator.TotalCountParameter,
            ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries,
            ReservedQueryParameterMatching.Ordinal,
            "traditional paging total-count request"
        ),
        new(
            CursorRequestValidator.PageTokenParameter,
            ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries,
            ReservedQueryParameterMatching.Ordinal,
            "cursor paging continuation token"
        ),
        new(
            CursorRequestValidator.PageSizeParameter,
            ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries,
            ReservedQueryParameterMatching.Ordinal,
            "cursor paging page size"
        ),
        new(
            ChangeVersionParameterValidator.MinChangeVersion,
            ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries,
            ReservedQueryParameterMatching.OrdinalIgnoreCase,
            "change-version window lower bound"
        ),
        new(
            ChangeVersionParameterValidator.MaxChangeVersion,
            ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries,
            ReservedQueryParameterMatching.OrdinalIgnoreCase,
            "change-version window upper bound"
        ),
        // Reserved on /partitions alone, which is the one asymmetry in this catalog. The count key is
        // generic enough to be a plausible resource property name, and canonicalizing or reserving it
        // on a collection GET would change filtering and unknown-field error text on every collection.
        // A schema declaring a query field of this name is refused at load all the same: a property
        // filterable on one operation and consumed as a control on its sibling is the defect DMS-1442
        // is about, not a difference worth preserving.
        new(
            PartitionRequestValidator.NumberParameter,
            ReservedQueryParameterOperations.Partitions,
            ReservedQueryParameterMatching.Ordinal,
            "partition count"
        ),
    ];

    private static readonly FrozenDictionary<string, ReservedQueryParameter> _byName =
        _all.ToFrozenDictionary(reserved => reserved.Name, StringComparer.OrdinalIgnoreCase);

    // Each projection is computed and widened to the read-only interface once, here, rather than on
    // every call. These are read on the request path, where returning the immutable array by interface
    // from a property would box the struct once per request for no gain.
    private static readonly IReadOnlyList<string> _ordinalCollectionGet = NamesOn(
        ReservedQueryParameterOperations.CollectionGet,
        ReservedQueryParameterMatching.Ordinal
    );

    private static readonly IReadOnlyList<string> _ordinalPartitions = NamesOn(
        ReservedQueryParameterOperations.Partitions,
        ReservedQueryParameterMatching.Ordinal
    );

    private static readonly IReadOnlyList<string> _ordinalChangeQueries = NamesOn(
        ReservedQueryParameterOperations.ChangeQueries,
        ReservedQueryParameterMatching.Ordinal
    );

    private static readonly IReadOnlyList<string> _ignoreCaseCollectionGet = NamesOn(
        ReservedQueryParameterOperations.CollectionGet,
        ReservedQueryParameterMatching.OrdinalIgnoreCase
    );

    private static readonly IReadOnlyList<string> _ignoreCasePartitions = NamesOn(
        ReservedQueryParameterOperations.Partitions,
        ReservedQueryParameterMatching.OrdinalIgnoreCase
    );

    private static readonly IReadOnlyList<string> _ignoreCaseChangeQueries = NamesOn(
        ReservedQueryParameterOperations.ChangeQueries,
        ReservedQueryParameterMatching.OrdinalIgnoreCase
    );

    /// <summary>
    /// Every reserved name, in the canonical order diagnostics report them.
    /// </summary>
    public static IReadOnlyList<ReservedQueryParameter> All { get; } = _all;

    /// <summary>
    /// Finds the reserved parameter a query field name collides with, comparing case-insensitively.
    /// </summary>
    /// <param name="queryFieldName">A query field name declared by a resource, or a supplied query key.</param>
    /// <param name="reserved">The colliding reserved parameter, or <see langword="null" /> when there is none.</param>
    /// <remarks>
    /// Case-insensitive whatever the entry's <see cref="ReservedQueryParameter.RequestMatching" /> says.
    /// A query field is matched against a supplied key with
    /// <see cref="StringComparison.OrdinalIgnoreCase" />, and the paging names are canonicalized
    /// case-insensitively at the HTTP boundary, so a field declared <c>PageSize</c> is exactly as
    /// unfilterable as one declared <c>pageSize</c>.
    /// </remarks>
    public static bool TryGetReserved(
        string queryFieldName,
        [NotNullWhen(true)] out ReservedQueryParameter? reserved
    )
    {
        ArgumentNullException.ThrowIfNull(queryFieldName);

        return _byName.TryGetValue(queryFieldName, out reserved);
    }

    /// <summary>
    /// The names <paramref name="operation" /> removes from resource-filter matching and compares
    /// ordinally, in canonical order.
    /// </summary>
    /// <param name="operation">Exactly one operation. A combination or <see cref="ReservedQueryParameterOperations.None" /> is rejected.</param>
    public static IReadOnlyList<string> OrdinalFilterExclusionsOn(
        ReservedQueryParameterOperations operation
    ) =>
        SingleOperation(operation) switch
        {
            ReservedQueryParameterOperations.CollectionGet => _ordinalCollectionGet,
            ReservedQueryParameterOperations.Partitions => _ordinalPartitions,
            _ => _ordinalChangeQueries,
        };

    /// <summary>
    /// The names <paramref name="operation" /> removes from resource-filter matching and compares
    /// case-insensitively, in canonical order.
    /// </summary>
    /// <param name="operation">Exactly one operation. A combination or <see cref="ReservedQueryParameterOperations.None" /> is rejected.</param>
    public static IReadOnlyList<string> IgnoreCaseFilterExclusionsOn(
        ReservedQueryParameterOperations operation
    ) =>
        SingleOperation(operation) switch
        {
            ReservedQueryParameterOperations.CollectionGet => _ignoreCaseCollectionGet,
            ReservedQueryParameterOperations.Partitions => _ignoreCasePartitions,
            _ => _ignoreCaseChangeQueries,
        };

    private static IReadOnlyList<string> NamesOn(
        ReservedQueryParameterOperations operation,
        ReservedQueryParameterMatching matching
    ) =>
        [
            .. _all.Where(reserved =>
                    reserved.ReservedOn.HasFlag(operation) && reserved.RequestMatching == matching
                )
                .Select(reserved => reserved.Name),
        ];

    /// <summary>
    /// Returns <paramref name="operation" /> when it names exactly one operation, and throws otherwise.
    /// </summary>
    /// <remarks>
    /// A combination is refused rather than answered, because the two readings of one - every name
    /// reserved on any of the operations, and only those reserved on all of them - are both plausible
    /// and produce different exclusion sets. Refusing makes the caller say which it meant.
    /// </remarks>
    private static ReservedQueryParameterOperations SingleOperation(
        ReservedQueryParameterOperations operation
    ) =>
        operation
            is ReservedQueryParameterOperations.CollectionGet
                or ReservedQueryParameterOperations.Partitions
                or ReservedQueryParameterOperations.ChangeQueries
            ? operation
            : throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Exactly one reserved query parameter operation must be named."
            );
}
