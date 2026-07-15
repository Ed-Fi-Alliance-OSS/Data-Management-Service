// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

/// <summary>
/// The authoritative cross-engine parity catalog for the relational redesign. Every equivalent
/// PostgreSQL and SQL Server case must exercise the same production boundary and assert the same
/// externally visible and authoritative storage semantics; identical filenames, SQL, or error
/// wording are not required. The design document mirrors this catalog as narrative and index.
/// </summary>
public static partial class ParityScenarioCatalog
{
    /// <summary>The nine canonical profile-layer scenario identifiers (preserved verbatim).</summary>
    public static readonly ImmutableArray<string> CanonicalProfileIds =
    [
        "ProfileVisibleRowUpdateWithHiddenRowPreservation",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation",
        "ProfileVisibleButAbsentNonCollectionScope",
        "ProfileHiddenInlinedColumnPreservation",
        "ProfileRootCreateRejectedWhenNonCreatable",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable",
        "ProfileHiddenExtensionRowPreservation",
        "ProfileHiddenExtensionChildCollectionPreservation",
        "ProfileUnchangedWriteGuardedNoOp",
    ];

    /// <summary>
    /// The eight canonical no-profile-layer scenario identifiers: the two original matrix rows
    /// plus the six families DMS-1285 will twin on SQL Server.
    /// </summary>
    public static readonly ImmutableArray<string> CanonicalNoProfileIds =
    [
        "NoProfileWriteBehavior",
        "FullSurfaceCollectionReorder",
        "NoProfileFullSurfaceCreate",
        "NoProfileChangedPutOmissionSemantics",
        "NoProfileGuardedNoOp",
        "NoProfileMultiBatchCollection",
        "NoProfilePostAsUpdate",
        "NoProfileRollbackSafety",
    ];

    private static IReadOnlyList<ParityScenario>? _all;

    /// <summary>
    /// The complete catalog across all three layers. Computed lazily so it never observes a
    /// partial-class layer array before that array's own static initializer has run.
    /// </summary>
    public static IReadOnlyList<ParityScenario> All =>
        _all ??= [.. ApiScenarios, .. ProfileScenarios, .. NoProfileScenarios];

    /// <summary>The canonical identifier of a row, i.e. the id without any <c>/variant</c> suffix.</summary>
    public static string CanonicalIdOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        int slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? id : id[..slash];
    }
}
