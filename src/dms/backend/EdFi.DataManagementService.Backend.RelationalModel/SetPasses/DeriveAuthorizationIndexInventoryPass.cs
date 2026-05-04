// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Backend.RelationalModel.Constraints;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Emits the indexes required by the Relationship-based and Namespace-based authorization
/// strategies (see <c>reference/design/backend-redesign/design-docs/auth.md</c> and
/// <c>compiled-mapping-set.md</c> §2.2 / §4.4).
/// </summary>
/// <remarks>
/// <para>The pass appends three categories of <see cref="DbIndexInfo"/> entries with
/// <see cref="DbIndexKind.Authorization"/> to <c>context.IndexInventory</c>:</para>
/// <list type="number">
///   <item>
///     <description>The five hardcoded PrimaryAssociation covering indexes
///     (StudentSchoolAssociation, StudentContactAssociation,
///     StaffEducationOrganizationAssignmentAssociation,
///     StaffEducationOrganizationEmploymentAssociation,
///     StudentEducationOrganizationResponsibilityAssociation), each with an INCLUDE column.</description>
///   </item>
///   <item>
///     <description>One index per resource that exposes an EducationOrganization securable
///     element, on the resolved root-table column. Skipped when a PrimaryAssociation index
///     above already covers the same <c>(table, key)</c>.</description>
///   </item>
///   <item>
///     <description>One index per resource that exposes a Namespace securable element, on
///     the resolved root-table column.</description>
///   </item>
/// </list>
/// <para>Person-join indexes (Student/Contact/Staff securable elements) are out of scope —
/// they are handled by DMS-1094.</para>
/// <para>Ordering invariant: this pass must run after <see cref="DeriveAuthHierarchyPass"/>
/// (so all auth-classified entries are appended together) and before
/// <c>ApplyDialectIdentifierShorteningPass</c> and <c>CanonicalizeOrderingPass</c> (so the new
/// indexes participate in dialect-aware identifier shortening and canonical ordering).</para>
/// </remarks>
public sealed class DeriveAuthorizationIndexInventoryPass : IRelationalModelSetPass
{
    private const string EdFiProjectName = "Ed-Fi";

    /// <summary>
    /// The five hardcoded PrimaryAssociation covering indexes from
    /// <c>auth.md</c> § "PrimaryAssociations should have the following indexes…".
    /// Each entry maps a resource to its (key column, INCLUDE column) pair on the root table.
    /// Names use the post-key-unification physical column names (e.g.
    /// <c>SchoolId_Unified</c>), which is what survives on the root table after
    /// <see cref="KeyUnificationPass"/> runs.
    /// </summary>
    private static readonly PrimaryAssociationIndex[] PrimaryAssociationIndexes =
    [
        new(
            new QualifiedResourceName(EdFiProjectName, "StudentSchoolAssociation"),
            new DbColumnName("SchoolId_Unified"),
            new DbColumnName("Student_DocumentId")
        ),
        new(
            new QualifiedResourceName(EdFiProjectName, "StudentContactAssociation"),
            new DbColumnName("Student_DocumentId"),
            new DbColumnName("Contact_DocumentId")
        ),
        new(
            new QualifiedResourceName(EdFiProjectName, "StaffEducationOrganizationAssignmentAssociation"),
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Staff_DocumentId")
        ),
        new(
            new QualifiedResourceName(EdFiProjectName, "StaffEducationOrganizationEmploymentAssociation"),
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Staff_DocumentId")
        ),
        new(
            new QualifiedResourceName(
                EdFiProjectName,
                "StudentEducationOrganizationResponsibilityAssociation"
            ),
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Student_DocumentId")
        ),
    ];

    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var concreteByName = context.ConcreteResourcesInNameOrder.ToDictionary(c => c.ResourceKey.Resource);

        var paIndexCovered = EmitPrimaryAssociationIndexes(context, concreteByName);
        EmitSecurableElementIndexes(context, paIndexCovered);
    }

    private static HashSet<(DbTableName Table, DbColumnName Column)> EmitPrimaryAssociationIndexes(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteByName
    )
    {
        var covered = new HashSet<(DbTableName Table, DbColumnName Column)>();

        foreach (var entry in PrimaryAssociationIndexes)
        {
            if (!concreteByName.TryGetValue(entry.Resource, out var concrete))
            {
                continue;
            }

            var rootTable = concrete.RelationalModel.Root;

            // Synthetic test fixtures may share a PA resource name without carrying the
            // post-key-unification PA columns; treat a missing literal as a silent skip rather
            // than fail the build. The authoritative golden manifests catch real schema drift.
            if (
                !TryResolveCanonicalColumn(rootTable, entry.KeyColumn, out var canonicalKey)
                || !TryResolveCanonicalColumn(rootTable, entry.IncludeColumn, out var canonicalInclude)
            )
            {
                continue;
            }

            context.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName(
                        ConstraintNaming.BuildAuthorizationIndexName(rootTable.Table, [canonicalKey])
                    ),
                    rootTable.Table,
                    KeyColumns: [canonicalKey],
                    IsUnique: false,
                    Kind: DbIndexKind.Authorization,
                    IncludeColumns: [canonicalInclude]
                )
            );

            covered.Add((rootTable.Table, canonicalKey));
        }

        return covered;
    }

    /// <summary>
    /// Emits authorization indexes for EducationOrganization and Namespace securable elements
    /// declared on each concrete resource. Indexes resolve to a single root-table column.
    /// </summary>
    /// <remarks>
    /// EdOrg paths are skipped when a PrimaryAssociation index already covers
    /// <c>(table, column)</c>. Namespace paths are not PA-deduped (Namespace is never a PA key
    /// column). Repeat emissions to the same <c>(table, column)</c> are coalesced globally —
    /// this protects index-name uniqueness when an EdOrg and Namespace path resolve to the
    /// same root column on a single resource, AND when multiple concrete resources share the
    /// same physical root table (e.g. descriptors backed by <c>dms.Descriptor</c>). Array-nested
    /// paths (containing <c>[*]</c>) are silently skipped (DMS-1094 scope).
    /// </remarks>
    private static void EmitSecurableElementIndexes(
        RelationalModelSetBuilderContext context,
        HashSet<(DbTableName Table, DbColumnName Column)> paIndexCovered
    )
    {
        var emitted = new HashSet<(DbTableName Table, DbColumnName Column)>();

        foreach (var concrete in context.ConcreteResourcesInNameOrder)
        {
            var rootTable = concrete.RelationalModel.Root;

            foreach (var jsonPath in concrete.SecurableElements.EducationOrganization.Select(e => e.JsonPath))
            {
                if (IsArrayNestedPath(jsonPath))
                {
                    continue;
                }

                var column = ResolveRootTableColumn(concrete, jsonPath);

                if (paIndexCovered.Contains((rootTable.Table, column)))
                {
                    continue;
                }

                AddSecurableElementIndex(context, rootTable.Table, column, emitted);
            }

            foreach (var namespacePath in concrete.SecurableElements.Namespace)
            {
                if (IsArrayNestedPath(namespacePath))
                {
                    continue;
                }

                var column = ResolveRootTableColumn(concrete, namespacePath);
                AddSecurableElementIndex(context, rootTable.Table, column, emitted);
            }
        }
    }

    private static void AddSecurableElementIndex(
        RelationalModelSetBuilderContext context,
        DbTableName table,
        DbColumnName column,
        HashSet<(DbTableName Table, DbColumnName Column)> emitted
    )
    {
        if (!emitted.Add((table, column)))
        {
            return;
        }

        context.IndexInventory.Add(
            new DbIndexInfo(
                new DbIndexName(ConstraintNaming.BuildAuthorizationIndexName(table, [column])),
                table,
                KeyColumns: [column],
                IsUnique: false,
                Kind: DbIndexKind.Authorization,
                IncludeColumns: null
            )
        );
    }

    /// <summary>
    /// Resolves a securable element JSON path to a single root-table canonical column.
    /// First checks <see cref="DocumentReferenceBinding"/>s on the root table for a matching
    /// <c>ReferenceJsonPath</c>; falls back to a scalar root-column whose <c>SourceJsonPath</c>
    /// matches the path (covers root-level scalars such as <c>$.namespace</c>). Throws when no
    /// match is found.
    /// </summary>
    private static DbColumnName ResolveRootTableColumn(ConcreteResourceModel concrete, string jsonPath)
    {
        var model = concrete.RelationalModel;
        var rootTable = model.Root;

        foreach (var binding in model.DocumentReferenceBindings)
        {
            if (binding.Table != rootTable.Table)
            {
                continue;
            }

            var identityBinding = binding.IdentityBindings.FirstOrDefault(ib =>
                string.Equals(ib.ReferenceJsonPath.Canonical, jsonPath, StringComparison.Ordinal)
            );

            if (identityBinding is not null)
            {
                return WalkUnifiedAlias(rootTable, identityBinding.Column);
            }
        }

        foreach (var column in rootTable.Columns)
        {
            if (
                column.SourceJsonPath is not null
                && string.Equals(column.SourceJsonPath.Value.Canonical, jsonPath, StringComparison.Ordinal)
            )
            {
                return WalkUnifiedAlias(rootTable, column.ColumnName);
            }
        }

        var resource = concrete.ResourceKey.Resource;
        throw new InvalidOperationException(
            $"Authorization index emission for '{resource.ProjectName}.{resource.ResourceName}' "
                + $"could not resolve securable element JSON path '{jsonPath}' to a column on root table "
                + $"'{rootTable.Table.Schema.Value}.{rootTable.Table.Name}'."
        );
    }

    /// <summary>
    /// Walks a single <see cref="ColumnStorage.UnifiedAlias"/> step to the canonical column;
    /// returns <paramref name="column"/> unchanged when the column does not exist or is stored.
    /// </summary>
    private static DbColumnName WalkUnifiedAlias(DbTableModel rootTable, DbColumnName column)
    {
        foreach (var col in rootTable.Columns)
        {
            if (col.ColumnName == column && col.Storage is ColumnStorage.UnifiedAlias alias)
            {
                return alias.CanonicalColumn;
            }
        }

        return column;
    }

    private static bool IsArrayNestedPath(string jsonPath) =>
        jsonPath.Contains("[*]", StringComparison.Ordinal);

    /// <summary>
    /// Resolves a literal column name on a root table to its canonical storage column by
    /// following any <see cref="ColumnStorage.UnifiedAlias.CanonicalColumn"/> indirection.
    /// Returns <see langword="false"/> when no column with the literal name exists on the
    /// table (the caller decides whether to skip silently or treat as an error).
    /// </summary>
    private static bool TryResolveCanonicalColumn(
        DbTableModel rootTable,
        DbColumnName literal,
        out DbColumnName canonical
    )
    {
        var column = rootTable.Columns.FirstOrDefault(c => c.ColumnName == literal);

        if (column is null)
        {
            canonical = default;
            return false;
        }

        canonical = column.Storage is ColumnStorage.UnifiedAlias alias
            ? alias.CanonicalColumn
            : column.ColumnName;
        return true;
    }

    private readonly record struct PrimaryAssociationIndex(
        QualifiedResourceName Resource,
        DbColumnName KeyColumn,
        DbColumnName IncludeColumn
    );
}
