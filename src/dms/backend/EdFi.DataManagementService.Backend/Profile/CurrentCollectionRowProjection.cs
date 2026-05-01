// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// A projection of one current collection-row used by the walker and the
/// <see cref="ProfileCollectionPlanner"/>. Built once when the per-merge index
/// <c>currentCollectionRowsByTableAndParentIdentity</c> is constructed.
/// </summary>
/// <remarks>
/// This projection is a strict superset of <see cref="CurrentCollectionRowSnapshot"/> —
/// the walker adapts to a snapshot at the planner-input use site via
/// <see cref="ToSnapshot"/> so the planner contract continues to consume the snapshot
/// shape unchanged.
/// </remarks>
/// <param name="ProjectedRow">Binding-indexed merged-row representation for overlay consumption.</param>
/// <param name="SemanticIdentityInOrder">Compiled semantic identity in deterministic binding order.</param>
/// <param name="StoredOrdinal">Stored ordinal column value.</param>
/// <param name="ParentPhysicalIdentityValues">Parent FK locator values; matches the index key.</param>
/// <param name="StableRowIdentity">Stable row identity (long) for by-id update/delete on the persister edge.</param>
/// <param name="CurrentRowByColumnName">
/// Column-name-keyed view of the hydrated row covering every column on the table model
/// (including UnifiedAlias columns absent from <c>ColumnBindings</c>). Consumed by the
/// matched-row overlay for hidden key-unification preservation.
/// </param>
internal sealed record CurrentCollectionRowProjection(
    RelationalWriteMergedTableRow ProjectedRow,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder,
    int StoredOrdinal,
    ImmutableArray<FlattenedWriteValue> ParentPhysicalIdentityValues,
    long StableRowIdentity,
    IReadOnlyDictionary<DbColumnName, object?> CurrentRowByColumnName
)
{
    /// <summary>
    /// Adapts this projection to the snapshot shape consumed by
    /// <see cref="ProfileCollectionPlanner"/> and the matched-row overlay. The projection
    /// is a strict superset of the snapshot, so this is a field-by-field projection.
    /// </summary>
    public CurrentCollectionRowSnapshot ToSnapshot() =>
        new(StableRowIdentity, SemanticIdentityInOrder, StoredOrdinal, ProjectedRow, CurrentRowByColumnName);
}
