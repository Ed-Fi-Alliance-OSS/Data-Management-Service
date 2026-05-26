// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Backend.RelationalModel.Constraints;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Emits the indexes required by the Relationship-based and Namespace-based authorization
/// strategies (see <c>reference/design/backend-redesign/design-docs/auth.md</c> and
/// <c>compiled-mapping-set.md</c> §2.2 / §4.4).
/// </summary>
/// <remarks>
/// <para>The pass appends four categories of <see cref="DbIndexInfo"/> entries with
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
///   <item>
///     <description>One index per join hop required to reach a Student / Contact / Staff
///     person resource from each independent declared root-scope securable element path. Each
///     index keys on the FK <c>_DocumentId</c> column at the hop and INCLUDEs the source table's
///     <c>DocumentId</c> (covering index for the runtime auth filter join). Behavior-aligned with
///     <c>EdFi.DataManagementService.Backend.Plans.SecurableElementColumnPathResolver.ResolvePersonPaths</c>:
///     every executable root-scope person path is indexed; array-nested paths (<c>[*]</c>) are
///     silently skipped when any other securable path resolves, or throw with the runtime's
///     "unsupported child-table traversal" message when no path resolves at all.</description>
///   </item>
/// </list>
/// <para>Auth indexes are deduped globally by <c>(table, leading key column)</c>. Person-join
/// emission may <em>widen</em> an existing auth index's <see cref="DbIndexInfo.IncludeColumns"/>
/// when a hop collides with the leading key of a PrimaryAssociation, EducationOrganization, or
/// Namespace index — the merged INCLUDE list is sorted ordinal-ascending by
/// <c>DbColumnName.Value</c> and deduped. PK/UK leading-column collisions on a person hop emit
/// a <em>separate</em> auth index — structural PK/UK indexes are never widened.</para>
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
    /// <summary>
    /// The five hardcoded PrimaryAssociation covering indexes from
    /// <c>auth.md</c> § "PrimaryAssociations should have the following indexes…".
    /// Each entry maps a resource to its (key column, INCLUDE column) pair on the root table.
    /// Column names come from <see cref="AuthNames"/> (single source of truth shared with the
    /// people auth views) and use the post-key-unification physical form (e.g.
    /// <c>SchoolId_Unified</c>), which is what survives on the root table after
    /// <see cref="KeyUnificationPass"/> runs.
    /// </summary>
    private static readonly PrimaryAssociationIndex[] PrimaryAssociationIndexes =
    [
        new(
            new QualifiedResourceName(PersonJoinPathResolver.EdFiProjectName, "StudentSchoolAssociation"),
            AuthNames.SchoolIdUnified,
            AuthNames.StudentDocumentId
        ),
        new(
            new QualifiedResourceName(PersonJoinPathResolver.EdFiProjectName, "StudentContactAssociation"),
            AuthNames.StudentDocumentId,
            AuthNames.ContactDocumentId
        ),
        new(
            new QualifiedResourceName(
                PersonJoinPathResolver.EdFiProjectName,
                "StaffEducationOrganizationAssignmentAssociation"
            ),
            AuthNames.EdOrgEdOrgId,
            AuthNames.StaffDocumentId
        ),
        new(
            new QualifiedResourceName(
                PersonJoinPathResolver.EdFiProjectName,
                "StaffEducationOrganizationEmploymentAssociation"
            ),
            AuthNames.EdOrgEdOrgId,
            AuthNames.StaffDocumentId
        ),
        new(
            new QualifiedResourceName(
                PersonJoinPathResolver.EdFiProjectName,
                "StudentEducationOrganizationResponsibilityAssociation"
            ),
            AuthNames.EdOrgEdOrgId,
            AuthNames.StudentDocumentId
        ),
    ];

    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Single source of truth for QualifiedResourceName → ConcreteResourceModel lookup —
        // shared across PA emission and the Person-join BFS so both branches apply the same
        // first-wins dup-handling rule documented on BuildResourceLookup.
        var resourceLookup = PersonJoinPathResolver.BuildResourceLookup(context.ConcreteResourcesInNameOrder);
        var pkUkLeadingColumns = BuildPkUkLeadingColumnSet(context.IndexInventory);

        // Shared lookup keyed by (table, leading key column) → emitted auth index.
        // PA, EdOrg, Namespace, and Person-join emission all consult and populate this.
        // Person-join emission may replace an entry in-place when widening IncludeColumns.
        var authIndexLookup = new Dictionary<(DbTableName Table, DbColumnName Column), DbIndexInfo>();

        EmitPrimaryAssociationIndexes(context, resourceLookup, authIndexLookup);

        // Per-resource resolution outcomes (anything that resolved — EdOrg/Namespace/Person —
        // regardless of whether an index was actually emitted by dedup). Threaded into the
        // Person-join pass so the array-nested rule can mirror the runtime's behavior:
        // silently skip array-nested person paths when some other securable path resolved,
        // throw with "unsupported child-table traversal" when no path resolved at all.
        var resourcesWithResolvedSecurable = new HashSet<QualifiedResourceName>();

        EmitSecurableElementIndexes(
            context,
            authIndexLookup,
            pkUkLeadingColumns,
            resourcesWithResolvedSecurable
        );

        EmitPersonJoinIndexes(context, authIndexLookup, resourceLookup, resourcesWithResolvedSecurable);
    }

    /// <summary>
    /// Builds the set of <c>(Table, leadingColumn)</c> pairs already covered by a PrimaryKey or
    /// UniqueConstraint index in the inventory. Single-column securable-element authorization
    /// indexes whose key column matches such a leading column are redundant — the unique index
    /// already supports the same equality lookup with no extra storage or write cost.
    /// PrimaryAssociation and Person-join indexes are not deduped this way because their
    /// <c>INCLUDE</c> column enables index-only scans that a plain PK/UK doesn't supply.
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

    private void EmitPrimaryAssociationIndexes(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteByName,
        Dictionary<(DbTableName Table, DbColumnName Column), DbIndexInfo> authIndexLookup
    )
    {
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
                    var missing = new List<string>(2);
                    if (canonicalKey is null)
                    {
                        missing.Add(entry.KeyColumn.Value);
                    }
                    if (canonicalInclude is null)
                    {
                        missing.Add(entry.IncludeColumn.Value);
                    }

                    throw new InvalidOperationException(
                        $"PrimaryAssociation '{entry.Resource.ProjectName}.{entry.Resource.ResourceName}' "
                            + $"is present in the model set but root table "
                            + $"'{rootTable.Table.Schema.Value}.{rootTable.Table.Name}' is missing literal "
                            + $"column(s) '{string.Join(", ", missing)}'. "
                            + $"Authorization index emission requires the post-key-unification column to exist."
                    );
                }

                // Synthetic test fixtures may share a PA resource name without carrying the
                // post-key-unification PA columns; treat as silent skip in default mode.
                continue;
            }

            var index = new DbIndexInfo(
                new DbIndexName(
                    ConstraintNaming.BuildAuthorizationIndexName(rootTable.Table, [canonicalKey.Value])
                ),
                rootTable.Table,
                KeyColumns: [canonicalKey.Value],
                IsUnique: false,
                Kind: DbIndexKind.Authorization,
                IncludeColumns: [canonicalInclude.Value]
            );

            context.IndexInventory.Add(index);
            authIndexLookup[(rootTable.Table, canonicalKey.Value)] = index;
        }
    }

    /// <summary>
    /// Emits authorization indexes for EducationOrganization and Namespace securable elements
    /// declared on each concrete resource. Indexes resolve to a single column on whichever table
    /// stores the value — root for non-nested paths, child for array-nested paths
    /// (e.g. <c>$.requiredAssessments[*].assessmentReference.namespace</c> resolves to the
    /// child collection table that holds the nested namespace identity scalar).
    /// </summary>
    /// <remarks>
    /// Both EdOrg and Namespace paths are skipped when an auth index already covers
    /// <c>(table, column)</c> via <paramref name="authIndexLookup"/> (PA seeds this lookup before
    /// the call) — current Ed-Fi schemas don't put Namespace on a PA key column, but symmetric
    /// coverage makes the pass robust to extension schemas that might. Emissions are also
    /// skipped when an existing PrimaryKey or UniqueConstraint index already leads on the same
    /// column (e.g. <c>UX_School_NK</c> on <c>edfi.School(SchoolId)</c> covers any auth equality
    /// lookup on <c>SchoolId</c>). Repeat emissions to the same <c>(table, column)</c> are
    /// coalesced globally — this protects index-name uniqueness when an EdOrg and Namespace path
    /// resolve to the same column on a single resource, AND when multiple concrete resources
    /// share the same physical table (e.g. descriptors backed by <c>dms.Descriptor</c>).
    /// </remarks>
    private static void EmitSecurableElementIndexes(
        RelationalModelSetBuilderContext context,
        Dictionary<(DbTableName Table, DbColumnName Column), DbIndexInfo> authIndexLookup,
        HashSet<(DbTableName Table, DbColumnName Column)> pkUkLeadingColumns,
        HashSet<QualifiedResourceName> resourcesWithResolvedSecurable
    )
    {
        foreach (var concrete in context.ConcreteResourcesInNameOrder)
        {
            var anyResolved = false;
            var unresolvedPaths = new List<string>();

            // Aggregate unresolved EdOrg + Namespace paths per resource and throw once for this
            // kind-group; mirrors EmitPersonJoinIndexes. Note that this pass surfaces EdOrg/
            // Namespace drift and Person drift in separate throws, so a schema author may see
            // them across two fix cycles; the runtime resolver (SecurableElementColumnPathResolver
            // .ResolveAll) collects all three kinds before throwing.
            foreach (var jsonPath in concrete.SecurableElements.EducationOrganization.Select(e => e.JsonPath))
            {
                var step = SecurableElementLocationResolver.ResolvePreferred(concrete, jsonPath);
                if (step is null)
                {
                    unresolvedPaths.Add(jsonPath);
                    continue;
                }
                anyResolved = true;
                AddSecurableElementIndex(
                    context,
                    step.SourceTable,
                    step.SourceColumnName,
                    authIndexLookup,
                    pkUkLeadingColumns
                );
            }

            foreach (var namespacePath in concrete.SecurableElements.Namespace)
            {
                var step = SecurableElementLocationResolver.ResolvePreferred(concrete, namespacePath);
                if (step is null)
                {
                    unresolvedPaths.Add(namespacePath);
                    continue;
                }
                anyResolved = true;
                AddSecurableElementIndex(
                    context,
                    step.SourceTable,
                    step.SourceColumnName,
                    authIndexLookup,
                    pkUkLeadingColumns
                );
            }

            if (unresolvedPaths.Count > 0)
            {
                var resource = concrete.ResourceKey.Resource;
                throw new InvalidOperationException(
                    $"Authorization index emission for '{resource.ProjectName}.{resource.ResourceName}' "
                        + $"could not resolve securable element JSON path(s): "
                        + $"{string.Join(", ", unresolvedPaths.Distinct(StringComparer.Ordinal))}."
                );
            }

            if (anyResolved)
            {
                resourcesWithResolvedSecurable.Add(concrete.ResourceKey.Resource);
            }
        }
    }

    private static void AddSecurableElementIndex(
        RelationalModelSetBuilderContext context,
        DbTableName table,
        DbColumnName column,
        Dictionary<(DbTableName Table, DbColumnName Column), DbIndexInfo> authIndexLookup,
        HashSet<(DbTableName Table, DbColumnName Column)> pkUkLeadingColumns
    )
    {
        if (authIndexLookup.ContainsKey((table, column)))
        {
            return;
        }

        if (pkUkLeadingColumns.Contains((table, column)))
        {
            return;
        }

        var index = new DbIndexInfo(
            new DbIndexName(ConstraintNaming.BuildAuthorizationIndexName(table, [column])),
            table,
            KeyColumns: [column],
            IsUnique: false,
            Kind: DbIndexKind.Authorization,
            IncludeColumns: null
        );

        context.IndexInventory.Add(index);
        authIndexLookup[(table, column)] = index;
    }

    /// <summary>
    /// Emits per-hop authorization indexes for Student / Contact / Staff securable elements.
    /// For each concrete resource declaring such an element, walks every independent declared
    /// root-scope person path from the subject root table to the person resource, and emits an
    /// auth index on each <c>(sourceTable, FK column)</c> hop that INCLUDEs the source table's
    /// <c>DocumentId</c>.
    /// Mirrors <c>SecurableElementColumnPathResolver.ResolvePersonPaths</c> so the runtime auth
    /// filter and the emitted indexes agree on which <c>(table, column)</c> carries each hop.
    /// </summary>
    /// <remarks>
    /// Subjects that ARE the person resource itself (e.g. Student with <c>$.studentUniqueId</c>)
    /// emit no person-join index for that kind — their own <c>DocumentId</c> is the auth anchor.
    /// Array-nested person paths (<c>[*]</c>) follow the runtime's resource-wide rule: silently
    /// skipped when any other securable path on the resource resolved; thrown with
    /// "unsupported child-table traversal" when no path resolved at all.
    /// </remarks>
    private static void EmitPersonJoinIndexes(
        RelationalModelSetBuilderContext context,
        Dictionary<(DbTableName Table, DbColumnName Column), DbIndexInfo> authIndexLookup,
        Dictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        HashSet<QualifiedResourceName> resourcesWithResolvedSecurable
    )
    {
        foreach (var concrete in context.ConcreteResourcesInNameOrder)
        {
            var resource = concrete.ResourceKey.Resource;
            var anyResolved = resourcesWithResolvedSecurable.Contains(resource);
            var skippedArrayNestedPaths = new List<string>();
            var unresolvedPaths = new List<string>();

            ProcessPersonKind(
                context,
                concrete,
                concrete.SecurableElements.Student,
                "Student",
                authIndexLookup,
                resourceLookup,
                ref anyResolved,
                skippedArrayNestedPaths,
                unresolvedPaths
            );
            ProcessPersonKind(
                context,
                concrete,
                concrete.SecurableElements.Contact,
                "Contact",
                authIndexLookup,
                resourceLookup,
                ref anyResolved,
                skippedArrayNestedPaths,
                unresolvedPaths
            );
            ProcessPersonKind(
                context,
                concrete,
                concrete.SecurableElements.Staff,
                "Staff",
                authIndexLookup,
                resourceLookup,
                ref anyResolved,
                skippedArrayNestedPaths,
                unresolvedPaths
            );

            // Mirrors SecurableElementColumnPathResolver.ResolveAll lines 132-151 — unresolved
            // person paths throw unconditionally; array-nested-only throws only when no other
            // path resolved on this resource.
            if (unresolvedPaths.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Authorization index emission for '{resource.ProjectName}.{resource.ResourceName}' "
                        + $"could not resolve person securable element JSON path(s): "
                        + $"{string.Join(", ", unresolvedPaths.Distinct(StringComparer.Ordinal))}."
                );
            }

            // When at least one person path resolved on this resource, array-nested siblings are
            // silently skipped to mirror the runtime resolver. Only the no-path-resolved case
            // surfaces array-nested paths as an error.
            if (!anyResolved && skippedArrayNestedPaths.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Authorization index emission for '{resource.ProjectName}.{resource.ResourceName}' "
                        + $"failed: all paths require unsupported child-table traversal (array-nested): "
                        + $"{string.Join(", ", skippedArrayNestedPaths.Distinct(StringComparer.Ordinal))}."
                );
            }
        }
    }

    /// <summary>
    /// Processes one person kind (Student / Contact / Staff) for a single subject resource.
    /// Resolves each declared root-scope person path independently and emits an auth index per
    /// hop of every resolved chain via <see cref="AddPersonJoinIndex"/>. Only the source side of
    /// each <see cref="ColumnPathStep"/> is consumed — the target side is the next resource's
    /// <c>DocumentId</c>, which is the next iteration's source.
    /// </summary>
    private static void ProcessPersonKind(
        RelationalModelSetBuilderContext context,
        ConcreteResourceModel subjectResource,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        Dictionary<(DbTableName Table, DbColumnName Column), DbIndexInfo> authIndexLookup,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        ref bool anyResolved,
        List<string> skippedArrayNestedPaths,
        List<string> unresolvedPaths
    )
    {
        if (personPaths.Count == 0)
        {
            return;
        }

        var subjectIsPersonResource = PersonJoinPathResolver.IsPersonResource(
            subjectResource.RelationalModel.Resource,
            personResourceName
        );

        foreach (var personPath in personPaths)
        {
            List<string> skippedPathsForElement = [];
            var chain = PersonJoinPathResolver.ResolveShortestPersonChain(
                subjectResource,
                [personPath],
                personResourceName,
                resourceLookup,
                skippedPathsForElement,
                out var unresolvedRootLevelPaths
            );

            skippedArrayNestedPaths.AddRange(skippedPathsForElement);

            if (chain is not null)
            {
                anyResolved = true;
                foreach (var step in chain)
                {
                    AddPersonJoinIndex(context, step.SourceTable, step.SourceColumnName, authIndexLookup);
                }
            }

            // Surface any root-level path that did not bind (Fix #7) — unless the subject IS the
            // person resource, in which case unresolved paths are self-references and silently
            // skipped (e.g. Student declaring $.studentUniqueId).
            if (unresolvedRootLevelPaths.Count > 0 && !subjectIsPersonResource)
            {
                unresolvedPaths.AddRange(unresolvedRootLevelPaths);
            }
        }
    }

    /// <summary>
    /// Emits or widens an authorization index for a single person-join hop.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>If an auth index already exists at <c>(sourceTable, canonicalFkColumn)</c>: when
    ///   its <see cref="DbIndexInfo.IncludeColumns"/> already contains <c>DocumentId</c>, skip;
    ///   otherwise replace it in <c>context.IndexInventory</c> with a new record whose merged
    ///   <see cref="DbIndexInfo.IncludeColumns"/> is sorted ordinal-ascending by
    ///   <c>DbColumnName.Value</c> and deduped. Applies uniformly to PA and EdOrg/Namespace
    ///   collisions.</item>
    ///   <item>If no auth index exists: emit a new auth index with
    ///   <c>IncludeColumns: [DocumentId]</c>. A PK/UK leading-column collision still emits a
    ///   <em>separate</em> auth index — the structural PK/UK is never widened.</item>
    /// </list>
    /// </remarks>
    private static void AddPersonJoinIndex(
        RelationalModelSetBuilderContext context,
        DbTableName sourceTable,
        DbColumnName canonicalFkColumn,
        Dictionary<(DbTableName Table, DbColumnName Column), DbIndexInfo> authIndexLookup
    )
    {
        var key = (sourceTable, canonicalFkColumn);

        if (authIndexLookup.TryGetValue(key, out var existing))
        {
            var currentIncludes = existing.IncludeColumns;
            if (
                currentIncludes is not null
                && currentIncludes.Any(c => c == RelationalNameConventions.DocumentIdColumnName)
            )
            {
                return;
            }

            var merged = (currentIncludes ?? [])
                .Concat([RelationalNameConventions.DocumentIdColumnName])
                .Distinct()
                .OrderBy(c => c.Value, StringComparer.Ordinal)
                .ToArray();

            var widened = existing with { IncludeColumns = merged };

            for (var i = 0; i < context.IndexInventory.Count; i++)
            {
                if (
                    context.IndexInventory[i].Name == existing.Name
                    && context.IndexInventory[i].Table == existing.Table
                )
                {
                    context.IndexInventory[i] = widened;
                    break;
                }
            }

            authIndexLookup[key] = widened;
            return;
        }

        var index = new DbIndexInfo(
            new DbIndexName(ConstraintNaming.BuildAuthorizationIndexName(sourceTable, [canonicalFkColumn])),
            sourceTable,
            KeyColumns: [canonicalFkColumn],
            IsUnique: false,
            Kind: DbIndexKind.Authorization,
            IncludeColumns: [RelationalNameConventions.DocumentIdColumnName]
        );

        context.IndexInventory.Add(index);
        authIndexLookup[key] = index;
    }

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
