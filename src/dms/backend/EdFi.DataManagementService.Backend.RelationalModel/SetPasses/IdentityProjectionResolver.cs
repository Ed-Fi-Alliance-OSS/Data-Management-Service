// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Shared identity-projection resolution used by both <see cref="DeriveTriggerInventoryPass"/> and the
/// tracked-change inventory derivation. Owns three concerns: identity-element / identity-JSON-path
/// resolution, canonical stored-column resolution (unified-alias unwrap + de-dup), and root
/// identity-projection-column derivation. Extracted so both passes share one implementation rather than
/// re-deriving identity semantics independently.
/// </summary>
internal static class IdentityProjectionResolver
{
    /// <summary>
    /// Builds identity element mappings for UUIDv5 computation by pairing each identity JSON path
    /// with exactly one column, preserving identity-path order and cardinality so the computed
    /// UUIDv5 hash matches Core's <c>ReferentialIdCalculator</c> (one element per document identity
    /// element). For identity-component references, this resolves to locally stored identity-part
    /// columns (not the FK <c>..._DocumentId</c>); when key unification fans one reference-site
    /// logical field out to multiple member columns, only the first member represents the path.
    /// Distinct identity paths are never merged, even when they share a canonical storage column.
    /// </summary>
    internal static IReadOnlyList<IdentityElementMapping> BuildIdentityElementMappings(
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        if (builderContext.IdentityJsonPaths.Count == 0)
        {
            return [];
        }

        var rootTable = resourceModel.Root;
        var rootColumnsByPath = BuildColumnNameLookupBySourceJsonPath(rootTable, resource);
        var referenceBindingsByIdentityPath = BuildReferenceIdentityBindings(
            resourceModel.DocumentReferenceBindings,
            resource
        );

        // Build a column-name-to-scalar-type lookup for type-aware identity hash formatting.
        var columnScalarTypes = rootTable
            .Columns.Where(c => c.ScalarType is not null)
            .ToDictionary(c => c.ColumnName.Value, c => c.ScalarType!, StringComparer.Ordinal);
        var columnKinds = rootTable.Columns.ToDictionary(
            c => c.ColumnName.Value,
            c => c.Kind,
            StringComparer.Ordinal
        );

        List<IdentityElementMapping> mappings = new(builderContext.IdentityJsonPaths.Count);

        foreach (var canonical in builderContext.IdentityJsonPaths.Select(p => p.Canonical))
        {
            var identityPartColumns = ResolveReferenceIdentityPartColumns(
                canonical,
                referenceBindingsByIdentityPath,
                rootTable.Table,
                resource
            );

            DbColumnName columnName;

            if (identityPartColumns is not null)
            {
                columnName = SelectIdentityElementColumn(identityPartColumns, rootTable, canonical, resource);
            }
            else if (!rootColumnsByPath.TryGetValue(canonical, out columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column during identity element mapping."
                );
            }

            mappings.Add(
                new IdentityElementMapping(
                    columnName,
                    canonical,
                    LookupColumnScalarType(columnScalarTypes, columnName, canonical, resource),
                    IsDescriptorReference(columnKinds, columnName, canonical, resource)
                )
            );
        }

        return mappings.ToArray();
    }

    /// <summary>
    /// Selects the single hash-element column representing one identity JSON path out of its
    /// same-site logical reference field group. Key unification can fan one identity path out to
    /// multiple physical member columns (persisted unified aliases over one canonical storage
    /// column); the hash must contain one element per identity path, so the first member in binding
    /// order represents the path. Every member must resolve to the same canonical storage column —
    /// members with different stored values cannot be collapsed into a single element.
    /// </summary>
    internal static DbColumnName SelectIdentityElementColumn(
        IReadOnlyList<DbColumnName> memberColumns,
        DbTableModel rootTable,
        string identityPath,
        QualifiedResourceName resource
    )
    {
        if (memberColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{identityPath}' on resource '{FormatResource(resource)}' "
                    + "resolved to an empty logical reference field group; at least one member "
                    + "column is required to select an identity hash element."
            );
        }

        var first = memberColumns[0];

        if (memberColumns.Count == 1)
        {
            return first;
        }

        var firstStoredColumn = ResolveToStoredColumn(first, rootTable, resource);

        foreach (var member in memberColumns.Skip(1))
        {
            var memberStoredColumn = ResolveToStoredColumn(member, rootTable, resource);

            if (memberStoredColumn != firstStoredColumn)
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath}' on resource '{FormatResource(resource)}' "
                        + $"resolved to member columns with different canonical storage columns "
                        + $"('{firstStoredColumn.Value}' vs '{memberStoredColumn.Value}'); a single "
                        + "identity hash element cannot represent more than one stored value."
                );
            }
        }

        return first;
    }

    /// <summary>
    /// Resolves the <see cref="RelationalScalarType"/> for a given column from the pre-built lookup.
    /// </summary>
    internal static RelationalScalarType LookupColumnScalarType(
        Dictionary<string, RelationalScalarType> columnScalarTypes,
        DbColumnName column,
        string identityPath,
        QualifiedResourceName resource
    )
    {
        if (!columnScalarTypes.TryGetValue(column.Value, out var scalarType))
        {
            throw new InvalidOperationException(
                $"Identity column '{column.Value}' for path '{identityPath}' on resource "
                    + $"'{FormatResource(resource)}' has no scalar type metadata."
            );
        }
        return scalarType;
    }

    private static bool IsDescriptorReference(
        Dictionary<string, ColumnKind> columnKinds,
        DbColumnName column,
        string identityPath,
        QualifiedResourceName resource
    )
    {
        if (!columnKinds.TryGetValue(column.Value, out var kind))
        {
            throw new InvalidOperationException(
                $"Identity column '{column.Value}' for path '{identityPath}' on resource "
                    + $"'{FormatResource(resource)}' has no column kind metadata."
            );
        }

        return kind is ColumnKind.DescriptorFk;
    }

    /// <summary>
    /// Resolves a reference-bearing identity path to its locally stored identity-part columns
    /// (from <see cref="DocumentReferenceBinding.IdentityBindings"/>). Returns <c>null</c> if the
    /// path does not match a reference binding, allowing the caller to fall through to direct
    /// column lookup.
    /// </summary>
    internal static IReadOnlyList<DbColumnName>? ResolveReferenceIdentityPartColumns(
        string canonicalPath,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingsByIdentityPath,
        DbTableName rootTable,
        QualifiedResourceName resource
    )
    {
        if (!referenceBindingsByIdentityPath.TryGetValue(canonicalPath, out var binding))
        {
            return null;
        }

        if (binding.Table != rootTable)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "must bind to the root table when resolving reference identity-part columns."
            );
        }

        if (!binding.IsIdentityComponent)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "mapped to a non-identity reference binding."
            );
        }

        if (binding.IdentityBindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "mapped to a reference binding with no identity-part columns."
            );
        }

        var identityBindingsForPath = binding
            .IdentityBindings.Where(ib => ib.ReferenceJsonPath.Canonical == canonicalPath)
            .ToArray();

        if (identityBindingsForPath.Length == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{canonicalPath}' on resource '{FormatResource(resource)}' "
                    + "did not resolve to a local identity-part column."
            );
        }

        // Grouped duplicate bindings share one ReferenceJsonPath but map to several key-unified target
        // identity fields. Order the field-name-matched binding first — the same rule that names abstract
        // identity columns — with a deterministic tie-break, so callers that take the representative member
        // (SelectIdentityElementColumn) produce a source independent of binding order.
        return OrderByRepresentativeReferenceBinding(
                identityBindingsForPath,
                binding.ReferenceObjectPath,
                ib => ib.ReferenceJsonPath,
                ib => ib.IdentityJsonPath
            )
            .Select(ib => ib.Column)
            .ToArray();
    }

    /// <summary>
    /// Builds the ordered set of root identity projection columns for MSSQL <c>UPDATE()</c> guards
    /// and PostgreSQL <c>IS DISTINCT FROM</c> comparisons. These columns must be physically stored
    /// (writable) columns because SQL Server rejects <c>UPDATE(computedCol)</c> at trigger creation
    /// time (Msg 2114). Under key unification, identity binding columns may be persisted computed
    /// aliases; this method resolves each to its canonical storage column and de-duplicates.
    /// </summary>
    internal static IReadOnlyList<DbColumnName> BuildRootIdentityProjectionColumns(
        RelationalResourceModel resourceModel,
        IReadOnlyList<IdentityElementMapping> identityElements,
        QualifiedResourceName resource
    )
    {
        if (identityElements.Count == 0)
        {
            return [];
        }

        var rootTable = resourceModel.Root;

        // Resolve each identity element column to its canonical stored column.
        // Under key unification, binding columns may be unified aliases that are computed,
        // and UPDATE() guards and IS DISTINCT FROM comparisons must reference stored columns.
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<DbColumnName> storedColumns = new(identityElements.Count);

        foreach (var element in identityElements)
        {
            var resolved = ResolveToStoredColumn(element.Column, rootTable, resource);

            if (seen.Add(resolved.Value))
            {
                storedColumns.Add(resolved);
            }
        }

        return storedColumns.ToArray();
    }

    /// <summary>
    /// Resolves a sequence of column names to their canonical stored columns, de-duplicating
    /// by canonical column name. Convenience wrapper around <see cref="ResolveToStoredColumn"/>.
    /// </summary>
    internal static IReadOnlyList<DbColumnName> ResolveColumnsToStored(
        IEnumerable<DbColumnName> columns,
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<DbColumnName> result = [];

        foreach (var column in columns)
        {
            var resolved = ResolveToStoredColumn(column, table, resource);

            if (seen.Add(resolved.Value))
            {
                result.Add(resolved);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Resolves a column name to its canonical stored column. If the column is a unified alias
    /// (persisted computed column), returns the canonical storage column; otherwise returns the
    /// column itself.
    /// </summary>
    /// <remarks>
    /// This is required for MSSQL trigger guards (<c>UPDATE()</c>) and propagation targets
    /// (<c>SET r.[col]</c>), which SQL Server rejects for computed columns
    /// (Msg 2114 and Msg 271 respectively).
    /// </remarks>
    internal static DbColumnName ResolveToStoredColumn(
        DbColumnName column,
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        var columnModel = table.Columns.FirstOrDefault(c => c.ColumnName == column);

        if (columnModel is null)
        {
            throw new InvalidOperationException(
                $"Trigger derivation for resource '{FormatResource(resource)}': column "
                    + $"'{column.Value}' not found on table "
                    + $"'{table.Table.Schema.Value}.{table.Table.Name}'."
            );
        }

        return columnModel.Storage switch
        {
            ColumnStorage.UnifiedAlias alias => alias.CanonicalColumn,
            _ => columnModel.ColumnName,
        };
    }

    /// <summary>
    /// Computes the <see cref="SourceValueSignature"/> for a propagation source column on the trigger
    /// table: it unwraps unified-alias storage to its canonical column and drops a non-nullable presence
    /// gate, which can never null the alias out and therefore cannot change its value. This is a strict
    /// refinement of <see cref="ResolveToStoredColumn"/> (canonical-only): two sources are value-equivalent
    /// only when they read the same canonical column under the same value-affecting presence gate.
    /// </summary>
    internal static SourceValueSignature ComputeSourceValueSignature(
        DbTableModel triggerTable,
        DbColumnName column,
        QualifiedResourceName resource
    )
    {
        var columnModel =
            triggerTable.Columns.FirstOrDefault(c => c.ColumnName == column)
            ?? throw new InvalidOperationException(
                $"Propagation trigger derivation for resource '{FormatResource(resource)}': source column "
                    + $"'{column.Value}' not found on trigger table "
                    + $"'{triggerTable.Table.Schema.Value}.{triggerTable.Table.Name}'."
            );

        if (columnModel.Storage is not ColumnStorage.UnifiedAlias alias)
        {
            return new SourceValueSignature(columnModel.ColumnName, NullablePresenceColumn: null);
        }

        DbColumnName? nullablePresenceColumn = null;

        if (alias.PresenceColumn is { } presenceColumn)
        {
            var presenceModel =
                triggerTable.Columns.FirstOrDefault(c => c.ColumnName == presenceColumn)
                ?? throw new InvalidOperationException(
                    $"Propagation trigger derivation for resource '{FormatResource(resource)}': presence "
                        + $"column '{presenceColumn.Value}' for unified alias '{column.Value}' not found on "
                        + $"trigger table '{triggerTable.Table.Schema.Value}.{triggerTable.Table.Name}'."
                );

            // A non-nullable presence gate can never take the CASE WHEN ... IS NULL branch, so the alias
            // always equals its canonical column and the gate does not distinguish the value.
            if (presenceModel.IsNullable)
            {
                nullablePresenceColumn = presenceColumn;
            }
        }

        return new SourceValueSignature(alias.CanonicalColumn, nullablePresenceColumn);
    }

    /// <summary>
    /// Validates that every candidate propagation source column resolved for one referrer target column
    /// reads the same effective value (equal <see cref="SourceValueSignature"/>). The MSSQL propagation
    /// trigger collapses key-unified identity bindings onto a single referrer column and emits one
    /// deterministic representative source; that collapse is sound only when the candidates are
    /// value-equivalent. A divergent shape — different canonical sources, or different nullable presence
    /// gates — would silently propagate a wrong value, so it fails loudly at derivation time instead.
    /// </summary>
    internal static void ValidatePropagationSourceEquivalence(
        DbTableModel triggerTable,
        QualifiedResourceName targetResource,
        QualifiedResourceName referrerResource,
        DbColumnName targetColumn,
        IReadOnlyList<(JsonPathExpression IdentityPath, DbColumnName SourceColumn)> candidates
    )
    {
        if (candidates.Count <= 1)
        {
            return;
        }

        var signatures = candidates
            .Select(candidate =>
                (
                    candidate.IdentityPath,
                    candidate.SourceColumn,
                    Signature: ComputeSourceValueSignature(
                        triggerTable,
                        candidate.SourceColumn,
                        targetResource
                    )
                )
            )
            .ToArray();

        if (signatures.Select(s => s.Signature).Distinct().Skip(1).Any())
        {
            var candidateDetails = string.Join(
                "; ",
                signatures.Select(s =>
                    $"path '{s.IdentityPath.Canonical}' -> column '{s.SourceColumn.Value}' (value "
                    + $"'{s.Signature.ValueColumn.Value}'"
                    + (
                        s.Signature.NullablePresenceColumn is { } presence
                            ? $", presence '{presence.Value}'"
                            : string.Empty
                    )
                    + ")"
                )
            );

            throw new InvalidOperationException(
                $"Propagation trigger derivation for target '{FormatResource(targetResource)}' referrer "
                    + $"'{FormatResource(referrerResource)}': the identity bindings collapsing onto referrer "
                    + $"column '{targetColumn.Value}' resolve to source columns with different effective "
                    + $"values on trigger table '{triggerTable.Table.Schema.Value}.{triggerTable.Table.Name}', "
                    + $"so a single representative source cannot stand in for them. Candidates: "
                    + $"{candidateDetails}."
            );
        }
    }
}

/// <summary>
/// The effective stored value that a propagation source column reads at runtime. Two source columns are
/// value-equivalent when their signatures are equal. A persisted unified alias is
/// <c>CASE WHEN presence IS NULL THEN NULL ELSE canonical END</c>, so aliases over the same canonical
/// column read the same value unless a <em>nullable</em> presence gate can diverge between them; a
/// non-nullable presence gate can never null the alias out and is excluded from the signature.
/// </summary>
/// <param name="ValueColumn">
/// The stored column whose value the source reads — the column itself when stored, or the unified-alias
/// canonical column.
/// </param>
/// <param name="NullablePresenceColumn">
/// The unified-alias presence gate when it is nullable (and can therefore change the value); otherwise
/// <see langword="null"/>.
/// </param>
internal sealed record SourceValueSignature(DbColumnName ValueColumn, DbColumnName? NullablePresenceColumn);
