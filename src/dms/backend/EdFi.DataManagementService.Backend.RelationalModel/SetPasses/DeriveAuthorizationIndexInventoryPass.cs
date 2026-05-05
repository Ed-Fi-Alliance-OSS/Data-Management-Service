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
/// <para><paramref name="throwOnMissingPaLiteral"/> selects the missing-literal contract:
/// strict pipeline (production runtime) sets it <see langword="true"/> so a missing PA literal
/// column raises <see cref="InvalidOperationException"/> and surfaces real schema drift;
/// default pipeline leaves it <see langword="false"/> so synthetic test fixtures
/// (<c>small/referential-identity</c>, <c>small/polymorphic</c>) that reuse PA resource names
/// without carrying the post-key-unification columns continue to build.</para>
/// </remarks>
/// <param name="throwOnMissingPaLiteral">
/// When <see langword="true"/>, throw if a PrimaryAssociation resource is present in the model
/// set but its root table is missing the expected literal key or include column. When
/// <see langword="false"/> (default), silently skip the PA emission instead.
/// </param>
public sealed class DeriveAuthorizationIndexInventoryPass(bool throwOnMissingPaLiteral = false)
    : IRelationalModelSetPass
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
        var pkUkLeadingColumns = BuildPkUkLeadingColumnSet(context.IndexInventory);

        var paIndexCovered = EmitPrimaryAssociationIndexes(context, concreteByName);
        EmitSecurableElementIndexes(context, paIndexCovered, pkUkLeadingColumns);
    }

    /// <summary>
    /// Builds the set of <c>(Table, leadingColumn)</c> pairs already covered by a PrimaryKey or
    /// UniqueConstraint index in the inventory. Single-column securable-element authorization
    /// indexes whose key column matches such a leading column are redundant — the unique index
    /// already supports the same equality lookup with no extra storage or write cost.
    /// PrimaryAssociation indexes are not deduped this way because their <c>INCLUDE</c> column
    /// (e.g. <c>Student_DocumentId</c>) enables index-only scans that a plain PK/UK doesn't supply.
    /// </summary>
    private static HashSet<(DbTableName Table, DbColumnName Column)> BuildPkUkLeadingColumnSet(
        IReadOnlyList<DbIndexInfo> inventory
    )
    {
        var set = new HashSet<(DbTableName Table, DbColumnName Column)>();

        foreach (var index in inventory)
        {
            if (
                index.Kind is DbIndexKind.PrimaryKey or DbIndexKind.UniqueConstraint
                && index.KeyColumns.Count > 0
            )
            {
                set.Add((index.Table, index.KeyColumns[0]));
            }
        }

        return set;
    }

    private HashSet<(DbTableName Table, DbColumnName Column)> EmitPrimaryAssociationIndexes(
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
            var canonicalKey = ResolveCanonical(rootTable, entry.KeyColumn);
            var canonicalInclude = ResolveCanonical(rootTable, entry.IncludeColumn);

            if (canonicalKey is null || canonicalInclude is null)
            {
                if (throwOnMissingPaLiteral)
                {
                    throw new InvalidOperationException(
                        $"PrimaryAssociation '{entry.Resource.ProjectName}.{entry.Resource.ResourceName}' "
                            + $"is present in the model set but root table "
                            + $"'{rootTable.Table.Schema.Value}.{rootTable.Table.Name}' is missing literal "
                            + $"column '{(canonicalKey is null ? entry.KeyColumn.Value : entry.IncludeColumn.Value)}'. "
                            + $"Authorization index emission requires the post-key-unification column to exist."
                    );
                }

                // Synthetic test fixtures may share a PA resource name without carrying the
                // post-key-unification PA columns; treat as silent skip in default mode.
                continue;
            }

            context.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName(
                        ConstraintNaming.BuildAuthorizationIndexName(rootTable.Table, [canonicalKey.Value])
                    ),
                    rootTable.Table,
                    KeyColumns: [canonicalKey.Value],
                    IsUnique: false,
                    Kind: DbIndexKind.Authorization,
                    IncludeColumns: [canonicalInclude.Value]
                )
            );

            covered.Add((rootTable.Table, canonicalKey.Value));
        }

        return covered;
    }

    /// <summary>
    /// Emits authorization indexes for EducationOrganization and Namespace securable elements
    /// declared on each concrete resource. Indexes resolve to a single column on whichever table
    /// stores the value — root for non-nested paths, child for array-nested paths
    /// (e.g. <c>$.requiredAssessments[*].assessmentReference.namespace</c> resolves to the
    /// child collection table that holds the nested namespace identity scalar).
    /// </summary>
    /// <remarks>
    /// Both EdOrg and Namespace paths are skipped when a PrimaryAssociation index already covers
    /// <c>(table, column)</c>. PA coverage is seeded into <c>emitted</c> so the dedup is uniform
    /// — current Ed-Fi schemas don't put Namespace on a PA key column, but symmetric coverage
    /// makes the pass robust to extension schemas that might. Emissions are also skipped when an
    /// existing PrimaryKey or UniqueConstraint index already leads on the same column (e.g.
    /// <c>UX_School_NK</c> on <c>edfi.School(SchoolId)</c> covers any auth equality lookup on
    /// <c>SchoolId</c>). Repeat emissions to the same <c>(table, column)</c> are coalesced
    /// globally — this protects index-name uniqueness when an EdOrg and Namespace path resolve
    /// to the same column on a single resource, AND when multiple concrete resources share the
    /// same physical table (e.g. descriptors backed by <c>dms.Descriptor</c>).
    /// </remarks>
    private static void EmitSecurableElementIndexes(
        RelationalModelSetBuilderContext context,
        HashSet<(DbTableName Table, DbColumnName Column)> paIndexCovered,
        HashSet<(DbTableName Table, DbColumnName Column)> pkUkLeadingColumns
    )
    {
        var emitted = new HashSet<(DbTableName Table, DbColumnName Column)>(paIndexCovered);

        foreach (var concrete in context.ConcreteResourcesInNameOrder)
        {
            foreach (var jsonPath in concrete.SecurableElements.EducationOrganization.Select(e => e.JsonPath))
            {
                var (table, column) = ResolveSecurableElementLocation(concrete, jsonPath);
                AddSecurableElementIndex(context, table, column, emitted, pkUkLeadingColumns);
            }

            foreach (var namespacePath in concrete.SecurableElements.Namespace)
            {
                var (table, column) = ResolveSecurableElementLocation(concrete, namespacePath);
                AddSecurableElementIndex(context, table, column, emitted, pkUkLeadingColumns);
            }
        }
    }

    private static void AddSecurableElementIndex(
        RelationalModelSetBuilderContext context,
        DbTableName table,
        DbColumnName column,
        HashSet<(DbTableName Table, DbColumnName Column)> emitted,
        HashSet<(DbTableName Table, DbColumnName Column)> pkUkLeadingColumns
    )
    {
        if (!emitted.Add((table, column)))
        {
            return;
        }

        if (pkUkLeadingColumns.Contains((table, column)))
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
    /// Resolves a securable element JSON path to its <c>(table, column)</c> location on the
    /// derived relational model. Walks every <see cref="DocumentReferenceBinding"/> (root or
    /// child) for an identity binding whose <c>ReferenceJsonPath</c> matches the securable path;
    /// falls back to scanning every table for a scalar column whose <c>SourceJsonPath</c>
    /// matches (covers root-level scalars such as <c>$.namespace</c>). Throws when no match is
    /// found.
    /// </summary>
    /// <remarks>
    /// <para>Array-nested paths (containing <c>[*]</c>) resolve onto the child collection table
    /// that owns the nested reference — for example
    /// <c>$.requiredAssessments[*].assessmentReference.namespace</c> on
    /// <c>edfi.GraduationPlan</c> resolves to
    /// <c>edfi.GraduationPlanRequiredAssessment.RequiredAssessmentAssessment_Namespace</c>.
    /// Non-nested paths still resolve to the root table because their bindings live there.</para>
    /// <para>This duplicates the EdOrg/Namespace branches of
    /// <c>EdFi.DataManagementService.Backend.Plans.SecurableElementColumnPathResolver</c>.
    /// The duplication is intentional: <c>Backend.Plans</c> references <c>Backend.RelationalModel</c>,
    /// so this pass cannot call the resolver directly without inverting the dependency.
    /// Person-join branches (Student/Contact/Staff) are out of scope here. When DMS-1094 lands
    /// the person-join indexes, consider extracting the shared resolution into a
    /// <c>Backend.RelationalModel</c>-side helper that both call sites can consume.</para>
    /// </remarks>
    private static (DbTableName Table, DbColumnName Column) ResolveSecurableElementLocation(
        ConcreteResourceModel concrete,
        string jsonPath
    )
    {
        var model = concrete.RelationalModel;

        foreach (var binding in model.DocumentReferenceBindings)
        {
            var identityBinding = binding.IdentityBindings.FirstOrDefault(ib =>
                string.Equals(ib.ReferenceJsonPath.Canonical, jsonPath, StringComparison.Ordinal)
            );

            if (identityBinding is null)
            {
                continue;
            }

            var owningTable = FindTableModel(model, binding.Table, concrete);
            var canonical = ResolveCanonical(owningTable, identityBinding.Column) ?? identityBinding.Column;
            return (binding.Table, canonical);
        }

        foreach (var table in model.TablesInDependencyOrder)
        {
            foreach (var column in table.Columns)
            {
                if (
                    column.SourceJsonPath is not null
                    && string.Equals(
                        column.SourceJsonPath.Value.Canonical,
                        jsonPath,
                        StringComparison.Ordinal
                    )
                )
                {
                    var canonical = ResolveCanonical(table, column.ColumnName) ?? column.ColumnName;
                    return (table.Table, canonical);
                }
            }
        }

        var resource = concrete.ResourceKey.Resource;
        throw new InvalidOperationException(
            $"Authorization index emission for '{resource.ProjectName}.{resource.ResourceName}' "
                + $"could not resolve securable element JSON path '{jsonPath}' to a column on any table."
        );
    }

    private static DbTableModel FindTableModel(
        RelationalResourceModel model,
        DbTableName tableName,
        ConcreteResourceModel concrete
    ) =>
        model.TablesInDependencyOrder.FirstOrDefault(t => t.Table == tableName)
        ?? throw new InvalidOperationException(
            $"DocumentReferenceBinding for '{concrete.ResourceKey.Resource.ResourceName}' references "
                + $"table '{tableName.Schema.Value}.{tableName.Name}' which is not present in "
                + "TablesInDependencyOrder."
        );

    /// <summary>
    /// Resolves a literal column name on a root table to its canonical storage column by
    /// following any <see cref="ColumnStorage.UnifiedAlias.CanonicalColumn"/> indirection.
    /// Returns <see langword="null"/> when no column with the literal name exists on the
    /// table — callers decide whether to treat that as a hard error or a silent skip.
    /// </summary>
    private static DbColumnName? ResolveCanonical(DbTableModel rootTable, DbColumnName literal)
    {
        var column = rootTable.Columns.FirstOrDefault(c => c.ColumnName == literal);

        if (column is null)
        {
            return null;
        }

        return column.Storage is ColumnStorage.UnifiedAlias alias ? alias.CanonicalColumn : column.ColumnName;
    }

    private readonly record struct PrimaryAssociationIndex(
        QualifiedResourceName Resource,
        DbColumnName KeyColumn,
        DbColumnName IncludeColumn
    );
}
