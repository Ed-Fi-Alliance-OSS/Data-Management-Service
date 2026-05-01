// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Shared test-double factories for profile-governance and root-table binding-classifier tests.
/// Keeps ResourceWritePlan construction minimal and parameterized so tests stay readable.
/// </summary>
internal static class ProfileTestDoubles
{
    private static readonly QualifiedResourceName _defaultResource = new("Ed-Fi", "Student");
    private static readonly DbSchemaName _defaultSchema = new("edfi");

    // ── Profile-request / profile-context factories ────────────────────────

    internal static ProfileAppliedWriteRequest CreateRequest(
        JsonNode? writableBody = null,
        bool rootResourceCreatable = true,
        params RequestScopeState[] scopeStates
    ) =>
        new(
            writableBody ?? new JsonObject(),
            rootResourceCreatable,
            scopeStates.ToImmutableArray(),
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

    internal static ProfileAppliedWriteContext CreateContext(
        ProfileAppliedWriteRequest request,
        JsonNode? visibleStoredBody = null,
        params StoredScopeState[] storedScopeStates
    ) =>
        new(
            request,
            visibleStoredBody ?? new JsonObject(),
            storedScopeStates.ToImmutableArray(),
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );

    internal static StoredScopeState StoredVisiblePresentScope(
        string scopeCanonical,
        params string[] hiddenMemberPaths
    ) =>
        new(
            new ScopeInstanceAddress(scopeCanonical, []),
            ProfileVisibilityKind.VisiblePresent,
            hiddenMemberPaths.ToImmutableArray()
        );

    internal static StoredScopeState StoredHiddenScope(string scopeCanonical) =>
        new(
            new ScopeInstanceAddress(scopeCanonical, []),
            ProfileVisibilityKind.Hidden,
            ImmutableArray<string>.Empty
        );

    internal static StoredScopeState StoredVisibleAbsentScope(
        string scopeCanonical,
        params string[] hiddenMemberPaths
    ) =>
        new(
            new ScopeInstanceAddress(scopeCanonical, []),
            ProfileVisibilityKind.VisibleAbsent,
            hiddenMemberPaths.ToImmutableArray()
        );

    internal static RequestScopeState RequestVisiblePresentScope(
        string scopeCanonical,
        bool creatable = true
    ) => new(new ScopeInstanceAddress(scopeCanonical, []), ProfileVisibilityKind.VisiblePresent, creatable);

    internal static RequestScopeState RequestVisibleAbsentScope(
        string scopeCanonical,
        bool creatable = true
    ) => new(new ScopeInstanceAddress(scopeCanonical, []), ProfileVisibilityKind.VisibleAbsent, creatable);

    internal static RequestScopeState RequestHiddenScope(string scopeCanonical, bool creatable = false) =>
        new(new ScopeInstanceAddress(scopeCanonical, []), ProfileVisibilityKind.Hidden, creatable);

    /// <summary>
    /// Builds a single-element semantic identity (one <see cref="SemanticIdentityPart"/>
    /// at <c>$.identityField0</c> with the supplied string value) for use as an
    /// ancestor-collection row identity in instance-aware scope address fixtures.
    /// </summary>
    internal static ImmutableArray<SemanticIdentityPart> SemanticIdentityForRow(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    /// <summary>
    /// Variant of <see cref="RequestVisiblePresentScope"/> that places the scope under a
    /// single ancestor collection instance pinned by <paramref name="ancestorIdentity"/>.
    /// Used by sibling-instance fixtures to assert per-instance scope-state filtering.
    /// </summary>
    internal static RequestScopeState RequestVisiblePresentScopeWithAncestors(
        string scopeCanonical,
        string ancestorJsonScope,
        ImmutableArray<SemanticIdentityPart> ancestorIdentity,
        bool creatable = true
    ) =>
        new(
            new ScopeInstanceAddress(
                scopeCanonical,
                ImmutableArray.Create(new AncestorCollectionInstance(ancestorJsonScope, ancestorIdentity))
            ),
            ProfileVisibilityKind.VisiblePresent,
            creatable
        );

    /// <summary>
    /// Variant of <see cref="RequestVisibleAbsentScope"/> that places the scope under a
    /// single ancestor collection instance pinned by <paramref name="ancestorIdentity"/>.
    /// </summary>
    internal static RequestScopeState RequestVisibleAbsentScopeWithAncestors(
        string scopeCanonical,
        string ancestorJsonScope,
        ImmutableArray<SemanticIdentityPart> ancestorIdentity,
        bool creatable = true
    ) =>
        new(
            new ScopeInstanceAddress(
                scopeCanonical,
                ImmutableArray.Create(new AncestorCollectionInstance(ancestorJsonScope, ancestorIdentity))
            ),
            ProfileVisibilityKind.VisibleAbsent,
            creatable
        );

    /// <summary>
    /// Variant of <see cref="StoredVisiblePresentScope"/> that places the scope under a
    /// single ancestor collection instance pinned by <paramref name="ancestorIdentity"/>.
    /// </summary>
    internal static StoredScopeState StoredVisiblePresentScopeWithAncestors(
        string scopeCanonical,
        string ancestorJsonScope,
        ImmutableArray<SemanticIdentityPart> ancestorIdentity,
        params string[] hiddenMemberPaths
    ) =>
        new(
            new ScopeInstanceAddress(
                scopeCanonical,
                ImmutableArray.Create(new AncestorCollectionInstance(ancestorJsonScope, ancestorIdentity))
            ),
            ProfileVisibilityKind.VisiblePresent,
            hiddenMemberPaths.ToImmutableArray()
        );

    // ── ResourceWritePlan builders ─────────────────────────────────────────

    /// <summary>
    /// Build a ResourceWritePlan with a single-column root table. The column is a Scalar
    /// whose relative path is <paramref name="scalarRelativePath"/>. Bindings: [0] = Scalar.
    /// </summary>
    internal static ResourceWritePlan BuildSingleScalarBindingRootPlan(
        string scalarRelativePath = "$.firstName"
    )
    {
        var scalarColumn = Column("FirstName", ColumnKind.Scalar, StringType());
        var rootModel = RootTable("Student", [scalarColumn]);
        var scalarBinding = new WriteColumnBinding(
            scalarColumn,
            new WriteValueSource.Scalar(Path(scalarRelativePath), StringType()),
            "FirstName"
        );
        var rootPlan = RootPlan(rootModel, [scalarBinding]);
        return WrapPlan(rootModel, [rootPlan], documentReferenceBindings: []);
    }

    /// <summary>
    /// Build a root plan with a KeyUnificationWritePlan whose single member is a
    /// ReferenceDerivedMember. Useful for tests that need the resolver's reference-rooted
    /// governance path. Bindings: [0] = canonical (Precomputed), [1] = ReferenceDerived
    /// member value column. No presence column.
    /// </summary>
    internal static (
        ResourceWritePlan Plan,
        int CanonicalBindingIndex,
        int MemberBindingIndex,
        DbColumnName MemberColumnName
    ) BuildRootPlanWithReferenceDerivedKeyUnificationMember(
        string referenceMemberPath,
        string derivedMemberPath
    )
    {
        var referenceObjectPath = Path(referenceMemberPath);
        var identitySegment = derivedMemberPath[(referenceMemberPath.Length + 1)..];

        var canonicalColumn = Column("KU_Canonical", ColumnKind.Scalar, Int32Type());
        var memberColumn = Column(
            "KU_" + SanitizePath(identitySegment) + "_FromReference",
            ColumnKind.Scalar,
            Int32Type(),
            sourceJsonPath: Path(derivedMemberPath)
        );
        var rootModel = RootTable("RefKUHost", [canonicalColumn, memberColumn]);

        var canonicalBinding = new WriteColumnBinding(
            canonicalColumn,
            new WriteValueSource.Precomputed(),
            canonicalColumn.ColumnName.Value
        );
        var referenceSource = new ReferenceDerivedValueSourceMetadata(
            BindingIndex: 0,
            ReferenceObjectPath: referenceObjectPath,
            IdentityJsonPath: Path("$." + identitySegment),
            ReferenceJsonPath: Path(derivedMemberPath)
        );
        var memberBinding = new WriteColumnBinding(
            memberColumn,
            new WriteValueSource.ReferenceDerived(referenceSource),
            memberColumn.ColumnName.Value
        );

        var keyUnificationPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: 0,
            MembersInOrder:
            [
                new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
                    MemberPathColumn: memberColumn.ColumnName,
                    RelativePath: Path(derivedMemberPath),
                    ReferenceSource: referenceSource,
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                ),
            ]
        );

        var rootPlan = RootPlan(
            rootModel,
            [canonicalBinding, memberBinding],
            keyUnificationPlans: [keyUnificationPlan]
        );

        // Register a DocumentReferenceBinding so the resource model carries the owning reference.
        var fkColumn = Column("UnusedRef_DocumentId", ColumnKind.DocumentFk, Int64Type());
        var docRefBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: referenceObjectPath,
            Table: rootModel.Table,
            FkColumn: fkColumn.ColumnName,
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings: []
        );

        return (
            WrapPlan(rootModel, [rootPlan], documentReferenceBindings: [docRefBinding]),
            0,
            1,
            memberColumn.ColumnName
        );
    }

    /// <summary>
    /// Build a root plan with a KeyUnificationWritePlan whose scalar member has a synthetic
    /// presence column. Returns the plan plus resolver-owned binding indices.
    /// Bindings: [0] = canonical scalar (Precomputed), [1] = synthetic presence (Precomputed),
    /// [2] = member-path scalar (Scalar).
    /// </summary>
    internal static (
        ResourceWritePlan Plan,
        int CanonicalBindingIndex,
        int SyntheticPresenceBindingIndex
    ) BuildRootPlanWithKeyUnificationPlan()
    {
        var canonicalColumn = Column("SchoolId_Canonical", ColumnKind.Scalar, Int32Type());
        var presenceColumn = Column("SchoolId_LocalAlias_Present", ColumnKind.Scalar, Int32Type());
        var memberColumn = Column(
            "SchoolId_LocalAlias",
            ColumnKind.Scalar,
            Int32Type(),
            sourceJsonPath: Path("$.localSchoolId")
        );
        var rootModel = RootTable("Thing", [canonicalColumn, presenceColumn, memberColumn]);

        var canonicalBinding = new WriteColumnBinding(
            canonicalColumn,
            new WriteValueSource.Precomputed(),
            "SchoolId_Canonical"
        );
        var presenceBinding = new WriteColumnBinding(
            presenceColumn,
            new WriteValueSource.Precomputed(),
            "SchoolId_LocalAlias_Present"
        );
        var memberBinding = new WriteColumnBinding(
            memberColumn,
            new WriteValueSource.Scalar(Path("$.localSchoolId"), Int32Type()),
            "SchoolId_LocalAlias"
        );

        var keyUnificationPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: 0,
            MembersInOrder:
            [
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: memberColumn.ColumnName,
                    RelativePath: Path("$.localSchoolId"),
                    ScalarType: Int32Type(),
                    PresenceColumn: presenceColumn.ColumnName,
                    PresenceBindingIndex: 1,
                    PresenceIsSynthetic: true
                ),
            ]
        );

        var rootPlan = RootPlan(
            rootModel,
            [canonicalBinding, presenceBinding, memberBinding],
            keyUnificationPlans: [keyUnificationPlan]
        );
        return (WrapPlan(rootModel, [rootPlan], documentReferenceBindings: []), 0, 1);
    }

    /// <summary>
    /// Build a root plan whose root table has one binding with WriteValueSource.ParentKeyPart
    /// (intentionally invalid for a root table — for the plan-shape-violation test).
    /// Bindings: [0] = ParentKeyPart.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlanWithParentKeyPartBinding()
    {
        var column = Column("ParentKeyAlien", ColumnKind.Scalar, Int32Type());
        var rootModel = RootTable("ThingRoot", [column]);
        var binding = new WriteColumnBinding(column, new WriteValueSource.ParentKeyPart(0), "ParentKeyAlien");
        var rootPlan = RootPlan(rootModel, [binding]);
        return WrapPlan(rootModel, [rootPlan], documentReferenceBindings: []);
    }

    /// <summary>
    /// Build a root plan whose root table has a DocumentReference FK binding followed by
    /// ReferenceDerived bindings. Bindings: [0] = DocumentReference FK,
    /// [1..] = ReferenceDerived entries in the supplied <paramref name="derivedMemberPaths"/> order.
    /// The RelationalResourceModel carries a matching DocumentReferenceBinding whose
    /// ReferenceObjectPath equals <paramref name="referenceMemberPath"/>.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlanWithDocumentReferenceBindings(
        string referenceMemberPath,
        string[] derivedMemberPaths
    )
    {
        var referenceObjectPath = Path(referenceMemberPath);
        var fkColumn = Column("Target_DocumentId", ColumnKind.DocumentFk, Int64Type());
        var derivedColumns = derivedMemberPaths
            .Select(
                (p, i) => Column($"Target_Ref{i}", ColumnKind.Scalar, Int32Type(), sourceJsonPath: Path(p))
            )
            .ToArray();

        var allColumns = new List<DbColumnModel> { fkColumn };
        allColumns.AddRange(derivedColumns);
        var rootModel = RootTable("RefHost", allColumns);

        var fkBinding = new WriteColumnBinding(
            fkColumn,
            new WriteValueSource.DocumentReference(0),
            "Target_DocumentId"
        );
        var derivedBindings = derivedColumns
            .Select(
                (col, i) =>
                    new WriteColumnBinding(
                        col,
                        new WriteValueSource.ReferenceDerived(
                            new ReferenceDerivedValueSourceMetadata(
                                BindingIndex: 0,
                                ReferenceObjectPath: referenceObjectPath,
                                IdentityJsonPath: Path(
                                    "$." + derivedMemberPaths[i][(referenceMemberPath.Length + 1)..]
                                ),
                                ReferenceJsonPath: Path(derivedMemberPaths[i])
                            )
                        ),
                        col.ColumnName.Value
                    )
            )
            .ToArray();

        var allBindings = new List<WriteColumnBinding> { fkBinding };
        allBindings.AddRange(derivedBindings);

        var documentReferenceBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: referenceObjectPath,
            Table: rootModel.Table,
            FkColumn: fkColumn.ColumnName,
            TargetResource: new QualifiedResourceName("Ed-Fi", "Target"),
            IdentityBindings: []
        );

        var rootPlan = RootPlan(rootModel, allBindings);
        return WrapPlan(rootModel, [rootPlan], documentReferenceBindings: [documentReferenceBinding]);
    }

    /// <summary>
    /// Build a root plan with a single DescriptorReference binding at <paramref name="relativePath"/>.
    /// Bindings: [0] = DescriptorReference.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlanWithDescriptorReferenceBinding(string relativePath)
    {
        var column = Column("SexDescriptor_DescriptorId", ColumnKind.DescriptorFk, Int64Type());
        var rootModel = RootTable("DescriptorHost", [column]);
        var binding = new WriteColumnBinding(
            column,
            new WriteValueSource.DescriptorReference(
                new QualifiedResourceName("Ed-Fi", "SexDescriptor"),
                Path(relativePath),
                DescriptorValuePath: null
            ),
            "SexDescriptor_DescriptorId"
        );
        var rootPlan = RootPlan(rootModel, [binding]);
        return WrapPlan(rootModel, [rootPlan], documentReferenceBindings: []);
    }

    /// <summary>
    /// Build a root plan with one Precomputed binding at [0] and one DocumentId binding at [1].
    /// </summary>
    internal static ResourceWritePlan BuildRootPlanWithPrecomputedAndDocumentIdBindings()
    {
        var preColumn = Column("PreCalc", ColumnKind.Scalar, Int32Type());
        var docIdColumn = Column("DocumentId", ColumnKind.ParentKeyPart, Int64Type());
        var rootModel = RootTable("StorageOnly", [preColumn, docIdColumn]);
        var bindings = new[]
        {
            new WriteColumnBinding(preColumn, new WriteValueSource.Precomputed(), "PreCalc"),
            new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
        };
        var rootPlan = RootPlan(rootModel, bindings);
        return WrapPlan(rootModel, [rootPlan], documentReferenceBindings: []);
    }

    /// <summary>
    /// Binding spec for <see cref="BuildRootPlusRootExtensionPlan"/>. Describes one
    /// column/binding to place on the <see cref="DbTableKind.RootExtension"/> child
    /// table in addition to the mandatory <c>DocumentId</c> parent-key binding.
    /// </summary>
    internal sealed record RootExtensionBindingSpec(
        string ColumnName,
        RootExtensionBindingKind Kind,
        string? RelativePath = null
    );

    internal enum RootExtensionBindingKind
    {
        Scalar,
        Precomputed,
        DocumentId,
    }

    /// <summary>
    /// Build a two-table plan: [0] = root ($), [1] = RootExtension child table at
    /// <paramref name="extensionJsonScope"/> carrying a ParentKeyPart DocumentId binding
    /// plus the supplied <paramref name="extensionBindings"/>. The extension table lives
    /// in its own <paramref name="extensionSchema"/> schema. Intended for
    /// <see cref="ProfileSeparateTableBindingClassifier"/> fixtures.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlusRootExtensionPlan(
        string extensionJsonScope = "$._ext.sample",
        string extensionSchema = "sample",
        params RootExtensionBindingSpec[] extensionBindings
    ) =>
        BuildRootPlusRootExtensionPlanCore(
            extensionJsonScope,
            extensionSchema,
            useScopeRelativeBindingPaths: false,
            extensionBindings
        );

    /// <summary>
    /// Variant of <see cref="BuildRootPlusRootExtensionPlan(string, string, RootExtensionBindingSpec[])"/>
    /// that emits binding <c>RelativePath</c> values in true scope-relative form (matching
    /// production <see cref="WritePlanCompiler"/> output via
    /// <c>WritePlanJsonPathConventions.DeriveScopeRelativePath</c>). The caller still supplies
    /// <see cref="RootExtensionBindingSpec.RelativePath"/> as the absolute path (so existing
    /// fixtures stay readable); the helper strips the extension scope prefix before stamping
    /// the binding source. Use this variant to exercise the non-root path-domain join in
    /// <see cref="ProfileBindingClassificationCore"/>.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlusRootExtensionPlanWithScopeRelativeBindingPaths(
        string extensionJsonScope = "$._ext.sample",
        string extensionSchema = "sample",
        params RootExtensionBindingSpec[] extensionBindings
    ) =>
        BuildRootPlusRootExtensionPlanCore(
            extensionJsonScope,
            extensionSchema,
            useScopeRelativeBindingPaths: true,
            extensionBindings
        );

    private static ResourceWritePlan BuildRootPlusRootExtensionPlanCore(
        string extensionJsonScope,
        string extensionSchema,
        bool useScopeRelativeBindingPaths,
        RootExtensionBindingSpec[] extensionBindings
    )
    {
        // Root table: single scalar so the root classifier has something to chew on if called.
        var rootScalar = Column("FirstName", ColumnKind.Scalar, StringType());
        var rootModel = RootTable("Host", [rootScalar]);
        var rootBinding = new WriteColumnBinding(
            rootScalar,
            new WriteValueSource.Scalar(Path("$.firstName"), StringType()),
            "FirstName"
        );
        var rootPlan = RootPlan(rootModel, [rootBinding]);

        // Extension table: mandatory ParentKeyPart DocumentId column + caller-supplied bindings.
        var extensionSchemaName = new DbSchemaName(extensionSchema);
        var extensionTableName = new DbTableName(extensionSchemaName, "HostExtension");
        var parentKeyColumn = Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false);

        var extensionColumns = new List<DbColumnModel> { parentKeyColumn };
        var extensionWriteBindings = new List<WriteColumnBinding>
        {
            new(parentKeyColumn, new WriteValueSource.ParentKeyPart(0), "DocumentId"),
        };

        foreach (var spec in extensionBindings)
        {
            (DbColumnModel column, WriteValueSource source) = spec.Kind switch
            {
                RootExtensionBindingKind.Scalar => (
                    Column(spec.ColumnName, ColumnKind.Scalar, StringType()),
                    (WriteValueSource)
                        new WriteValueSource.Scalar(
                            Path(
                                ConformBindingRelativePath(
                                    spec.RelativePath
                                        ?? throw new ArgumentException(
                                            "Scalar binding spec must supply a RelativePath.",
                                            nameof(extensionBindings)
                                        ),
                                    extensionJsonScope,
                                    useScopeRelativeBindingPaths
                                )
                            ),
                            StringType()
                        )
                ),
                RootExtensionBindingKind.Precomputed => (
                    Column(spec.ColumnName, ColumnKind.Scalar, Int32Type()),
                    new WriteValueSource.Precomputed()
                ),
                RootExtensionBindingKind.DocumentId => (
                    Column(spec.ColumnName, ColumnKind.ParentKeyPart, Int64Type()),
                    new WriteValueSource.DocumentId()
                ),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(extensionBindings),
                    $"Unsupported RootExtensionBindingKind '{spec.Kind}'."
                ),
            };
            extensionColumns.Add(column);
            extensionWriteBindings.Add(new WriteColumnBinding(column, source, spec.ColumnName));
        }

        var extensionTableModel = new DbTableModel(
            Table: extensionTableName,
            JsonScope: Path(extensionJsonScope),
            Key: new TableKey(
                "PK_HostExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: extensionColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var extensionPlan = new TableWritePlan(
            TableModel: extensionTableModel,
            InsertSql: $"INSERT INTO {extensionSchema}.\"HostExtension\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(
                1000,
                Math.Max(1, extensionWriteBindings.Count),
                65535
            ),
            ColumnBindings: extensionWriteBindings,
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: [rootModel, extensionTableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, extensionPlan]
        );
    }

    /// <summary>
    /// Build a two-table plan whose separate table is tagged as a configurable
    /// non-<see cref="DbTableKind.RootExtension"/> kind.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlusSeparateTablePlanWithNonRootExtensionKind(
        DbTableKind nonRootExtensionKind = DbTableKind.CollectionExtensionScope
    )
    {
        var rootScalar = Column("FirstName", ColumnKind.Scalar, StringType());
        var rootModel = RootTable("Host", [rootScalar]);
        var rootBinding = new WriteColumnBinding(
            rootScalar,
            new WriteValueSource.Scalar(Path("$.firstName"), StringType()),
            "FirstName"
        );
        var rootPlan = RootPlan(rootModel, [rootBinding]);

        var extensionSchemaName = new DbSchemaName("sample");
        var parentKeyColumn = Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false);
        var scalarColumn = Column("FavoriteColor", ColumnKind.Scalar, StringType());

        var tableModel = new DbTableModel(
            Table: new DbTableName(extensionSchemaName, "HostExtension"),
            JsonScope: Path("$._ext.sample"),
            Key: new TableKey(
                "PK_HostExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [parentKeyColumn, scalarColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: nonRootExtensionKind,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var extensionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO sample.\"HostExtension\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 2, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(parentKeyColumn, new WriteValueSource.ParentKeyPart(0), "DocumentId"),
                new WriteColumnBinding(
                    scalarColumn,
                    new WriteValueSource.Scalar(Path("$._ext.sample.favoriteColor"), StringType()),
                    "FavoriteColor"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: [rootModel, tableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, extensionPlan]
        );
    }

    /// <summary>
    /// Build a three-table plan: [0] = root ($), [1] = RootExtension child table,
    /// [2] = an additional separate table (default:
    /// <see cref="DbTableKind.CollectionExtensionScope"/>) that the slice-3 synthesizer
    /// must silently skip. Used to exercise the synthesizer's "carry unused tables
    /// through" behavior when the write plan contains non-root-extension scopes the
    /// current request never touches.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlusRootExtensionPlusUnusedTablePlan(
        RootExtensionBindingSpec extensionBinding,
        string unusedTableJsonScope = "$._ext.sample.addresses[*]",
        string unusedTableSchema = "sample",
        DbTableKind unusedTableKind = DbTableKind.CollectionExtensionScope
    )
    {
        // Start from the two-table plan so the root + RootExtension shape stays identical
        // to every other slice-3 fixture.
        var twoTablePlan = BuildRootPlusRootExtensionPlan(extensionBindings: extensionBinding);
        var rootModel = twoTablePlan.TablePlansInDependencyOrder[0].TableModel;
        var rootPlan = twoTablePlan.TablePlansInDependencyOrder[0];
        var extensionPlan = twoTablePlan.TablePlansInDependencyOrder[1];

        // Third table: a minimal unused separate table tagged with the non-RootExtension
        // kind. The synthesizer must not process it in slice 3.
        var unusedSchemaName = new DbSchemaName(unusedTableSchema);
        var unusedParentKeyColumn = Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false);
        var unusedScalarColumn = Column("Street", ColumnKind.Scalar, StringType());
        var unusedTableModel = new DbTableModel(
            Table: new DbTableName(unusedSchemaName, "HostExtensionAddress"),
            JsonScope: Path(unusedTableJsonScope),
            Key: new TableKey(
                "PK_HostExtensionAddress",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [unusedParentKeyColumn, unusedScalarColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: unusedTableKind,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };
        var unusedTablePlan = new TableWritePlan(
            TableModel: unusedTableModel,
            InsertSql: $"INSERT INTO {unusedTableSchema}.\"HostExtensionAddress\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 2, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    unusedParentKeyColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    unusedScalarColumn,
                    new WriteValueSource.Scalar(Path($"{unusedTableJsonScope}.street"), StringType()),
                    "Street"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: [rootModel, extensionPlan.TableModel, unusedTableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, extensionPlan, unusedTablePlan]
        );
    }

    /// <summary>
    /// Build a two-table plan: [0] = root ($), [1] = RootExtension child table at
    /// <c>$._ext.sample</c> carrying both a direct-scope scalar binding (under
    /// <c>$._ext.sample.someDirect</c>) and a descendant inlined non-collection scope's
    /// scalar binding under <paramref name="descendantBindingRelativePath"/>. The
    /// descendant scope at <paramref name="descendantScopeRelativePath"/> is inlined onto
    /// the same RootExtension table — so
    /// <see cref="ProfileBindingClassificationCore.ResolveOwnerTablePlan"/> for the
    /// descendant scope returns the same table as the direct scope. Used to exercise
    /// CP5's descendant scope-state collector.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlusRootExtensionPlanWithInlinedDescendantScope(
        string descendantScopeRelativePath = "$._ext.sample.detail",
        string descendantBindingRelativePath = "$._ext.sample.detail.someField"
    ) =>
        BuildRootPlusRootExtensionPlan(
            extensionJsonScope: "$._ext.sample",
            extensionSchema: "sample",
            new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            ),
            new RootExtensionBindingSpec(
                "DetailField",
                RootExtensionBindingKind.Scalar,
                RelativePath: descendantBindingRelativePath
            )
        );

    /// <summary>
    /// Build a three-table plan: [0] = root ($), [1] = RootExtension child table at
    /// <c>$._ext.sample</c>, [2] = a separate child collection table whose JSON scope is
    /// <paramref name="childScopeRelativePath"/>. The child scope therefore lives on its
    /// own table (not inlined onto the RootExtension). Used to exercise CP5's
    /// descendant scope-state collector when the descendant is owned by a different
    /// table — which must be excluded from the collected set.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlusRootExtensionWithSeparateChildTablePlan(
        string childScopeRelativePath = "$._ext.sample.subCollection"
    )
    {
        // Start from the standard root + RootExtension shape with a single direct-scope
        // scalar so the descendant table has a non-empty sibling to compete against.
        var twoTablePlan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var rootModel = twoTablePlan.TablePlansInDependencyOrder[0].TableModel;
        var rootPlan = twoTablePlan.TablePlansInDependencyOrder[0];
        var extensionPlan = twoTablePlan.TablePlansInDependencyOrder[1];

        // Third table: a separate child whose JsonScope is the descendant scope. Tag it
        // CollectionExtensionScope (a non-collection 1:1 extension kind that does not
        // require a CollectionMergePlan) so the table is clearly distinct from the
        // RootExtension table hosting the direct scope.
        var childSchemaName = new DbSchemaName("sample");
        var childParentKeyColumn = Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false);
        var childScalarColumn = Column("Caption", ColumnKind.Scalar, StringType());

        var childTableModel = new DbTableModel(
            Table: new DbTableName(childSchemaName, "HostExtensionSubCollection"),
            JsonScope: Path(childScopeRelativePath),
            Key: new TableKey(
                "PK_HostExtensionSubCollection",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [childParentKeyColumn, childScalarColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.CollectionExtensionScope,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var childTablePlan = new TableWritePlan(
            TableModel: childTableModel,
            InsertSql: "INSERT INTO sample.\"HostExtensionSubCollection\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 2, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    childParentKeyColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    childScalarColumn,
                    new WriteValueSource.Scalar(Path($"{childScopeRelativePath}.caption"), StringType()),
                    "Caption"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: [rootModel, extensionPlan.TableModel, childTableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, extensionPlan, childTablePlan]
        );
    }

    /// <summary>
    /// Build a three-table plan: [0] = root ($), [1] = parents collection at
    /// <c>$.parents[*]</c>, [2] = a <see cref="DbTableKind.CollectionExtensionScope"/>
    /// aligned to each parent row at <c>$.parents[*]._ext.aligned</c>. The aligned table
    /// carries both the direct-scope scalar binding and a descendant inlined
    /// non-collection scope's scalar binding under
    /// <paramref name="descendantBindingRelativePath"/>. The descendant scope at
    /// <paramref name="descendantScopeRelativePath"/> is inlined onto the aligned table.
    /// Used to exercise CP5's descendant scope-state collector under sibling
    /// collection-row instances.
    /// </summary>
    internal static ResourceWritePlan BuildCollectionWithAlignedExtensionAndInlinedDescendantPlan(
        string descendantScopeRelativePath = "$.parents[*]._ext.aligned.detail",
        string descendantBindingRelativePath = "$.parents[*]._ext.aligned.detail.someField"
    )
    {
        const string ParentsScope = "$.parents[*]";
        const string AlignedScope = "$.parents[*]._ext.aligned";

        // Root table: trivial DocumentId-only shape — irrelevant to the collector test.
        var rootDocId = Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false);
        var rootModel = new DbTableModel(
            Table: new DbTableName(_defaultSchema, "AlignedTest"),
            JsonScope: Path("$"),
            Key: new TableKey(
                "PK_AlignedTest",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [rootDocId],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        var rootPlan = new TableWritePlan(
            TableModel: rootModel,
            InsertSql: "INSERT INTO edfi.\"AlignedTest\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(rootDocId, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );

        // Parents collection: collection-key item id, parent doc id, ordinal, identity scalar.
        // Tagged as Collection — this kind requires a CollectionMergePlan, mirroring real
        // write-plan shape produced by the compiler for top-level collections.
        var parentsItemIdColumn = Column("ParentItemId", ColumnKind.CollectionKey, null, isNullable: false);
        var parentsDocIdColumn = Column(
            "ParentDocumentId",
            ColumnKind.ParentKeyPart,
            null,
            isNullable: false
        );
        var parentsOrdinalColumn = Column("Ordinal", ColumnKind.Ordinal, null, isNullable: false);
        var parentsIdentityColumn = Column(
            "IdentityField0",
            ColumnKind.Scalar,
            StringType(),
            sourceJsonPath: Path("$.identityField0")
        );
        var parentsTableModel = new DbTableModel(
            Table: new DbTableName(_defaultSchema, "Parents"),
            JsonScope: Path(ParentsScope),
            Key: new TableKey(
                "PK_Parents",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: [parentsItemIdColumn, parentsDocIdColumn, parentsOrdinalColumn, parentsIdentityColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        Path("$.identityField0"),
                        parentsIdentityColumn.ColumnName
                    ),
                ]
            ),
        };
        var parentsPlan = new TableWritePlan(
            TableModel: parentsTableModel,
            InsertSql: "INSERT INTO edfi.\"Parents\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 4, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    parentsItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    parentsDocIdColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(parentsOrdinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    parentsIdentityColumn,
                    new WriteValueSource.Scalar(Path("$.identityField0"), StringType()),
                    "IdentityField0"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(Path("$.identityField0"), 3),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"Parents\" SET X = @X WHERE \"ParentItemId\" = @ParentItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"Parents\" WHERE \"ParentItemId\" = @ParentItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ParentItemId"),
                0
            )
        );

        // Aligned extension table: ParentKeyPart + direct-scope scalar + inlined-descendant
        // scalar. JsonScope == $.parents[*]._ext.aligned. Tagged CollectionExtensionScope.
        var alignedSchemaName = new DbSchemaName("sample");
        var alignedParentKeyColumn = Column(
            "ParentItemId",
            ColumnKind.ParentKeyPart,
            null,
            isNullable: false
        );
        var alignedFavoriteColumn = Column(
            "FavoriteColor",
            ColumnKind.Scalar,
            StringType(),
            sourceJsonPath: Path($"{AlignedScope}.favoriteColor")
        );
        var alignedDetailColumn = Column(
            "DetailField",
            ColumnKind.Scalar,
            StringType(),
            sourceJsonPath: Path(descendantBindingRelativePath)
        );

        var alignedTableModel = new DbTableModel(
            Table: new DbTableName(alignedSchemaName, "ParentsAlignedExtension"),
            JsonScope: Path(AlignedScope),
            Key: new TableKey(
                "PK_ParentsAlignedExtension",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [alignedParentKeyColumn, alignedFavoriteColumn, alignedDetailColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.CollectionExtensionScope,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentItemId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
                SemanticIdentityBindings: []
            ),
        };
        var alignedPlan = new TableWritePlan(
            TableModel: alignedTableModel,
            InsertSql: "INSERT INTO sample.\"ParentsAlignedExtension\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 3, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    alignedParentKeyColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    alignedFavoriteColumn,
                    new WriteValueSource.Scalar(Path($"{AlignedScope}.favoriteColor"), StringType()),
                    "FavoriteColor"
                ),
                new WriteColumnBinding(
                    alignedDetailColumn,
                    new WriteValueSource.Scalar(Path(descendantBindingRelativePath), StringType()),
                    "DetailField"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: [rootModel, parentsTableModel, alignedTableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, alignedPlan]
        );
    }

    /// <summary>
    /// Build a two-table plan: [0] = root ($), [1] = RootExtension child table at
    /// <paramref name="extensionJsonScope"/> with a key-unification plan whose members
    /// live under the extension scope. Mirrors
    /// <see cref="BuildRootPlanWithKeyUnificationMembers"/> for separate-table resolver
    /// fixtures. Bindings on the extension table: [0] = ParentKeyPart DocumentId, then
    /// [1] = canonical (Precomputed), followed by per-member synthetic presence
    /// (Precomputed, when requested) and member value (Scalar / Descriptor) bindings
    /// in the same order the root builder produces.
    /// </summary>
    internal static (
        ResourceWritePlan Plan,
        int ExtensionCanonicalBindingIndex,
        IReadOnlyDictionary<string, int> ExtensionPresenceBindingIndicesByRelativePath
    ) BuildRootPlusRootExtensionPlanWithKeyUnification(
        IReadOnlyList<KeyUnificationMemberSpec> members,
        string extensionJsonScope = "$._ext.sample",
        string extensionSchema = "sample",
        bool canonicalIsNullable = true
    )
    {
        if (members is null || members.Count == 0)
        {
            throw new ArgumentException("At least one member spec is required.", nameof(members));
        }

        // Root table: trivial scalar to populate the root plan. Not touched by the
        // separate-table resolver — the resolver only operates on the extension plan.
        var rootScalar = Column("FirstName", ColumnKind.Scalar, StringType());
        var rootModel = RootTable("Host", [rootScalar]);
        var rootBinding = new WriteColumnBinding(
            rootScalar,
            new WriteValueSource.Scalar(Path("$.firstName"), StringType()),
            "FirstName"
        );
        var rootPlan = RootPlan(rootModel, [rootBinding]);

        // Extension table: ParentKeyPart DocumentId column at [0], then canonical +
        // per-member (optional synthetic presence + value) columns in order.
        var extensionSchemaName = new DbSchemaName(extensionSchema);
        var extensionTableName = new DbTableName(extensionSchemaName, "HostExtension");
        var parentKeyColumn = Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false);
        var canonicalColumn = Column(
            CanonicalColumnFor().Value,
            ColumnKind.Scalar,
            Int32Type(),
            isNullable: canonicalIsNullable
        );

        var extensionColumns = new List<DbColumnModel> { parentKeyColumn, canonicalColumn };
        var extensionBindings = new List<WriteColumnBinding>
        {
            new(parentKeyColumn, new WriteValueSource.ParentKeyPart(0), "DocumentId"),
            new(canonicalColumn, new WriteValueSource.Precomputed(), canonicalColumn.ColumnName.Value),
        };
        var canonicalBindingIndex = 1;

        var presenceBindingIndicesByRelativePath = new Dictionary<string, int>(StringComparer.Ordinal);
        var memberColumnsByRelativePath = new Dictionary<string, DbColumnModel>(StringComparer.Ordinal);
        var presenceColumnsByRelativePath = new Dictionary<string, DbColumnModel>(StringComparer.Ordinal);

        foreach (var spec in members)
        {
            var memberColumn = Column(
                spec.MemberColumnName ?? MemberPathColumnFor(spec.RelativePath).Value,
                ColumnKind.Scalar,
                spec.SourceKind == KeyUnificationMemberSourceKind.Scalar ? Int32Type() : Int64Type(),
                sourceJsonPath: Path(spec.RelativePath)
            );
            memberColumnsByRelativePath[spec.RelativePath] = memberColumn;
            extensionColumns.Add(memberColumn);

            if (spec.PresenceSynthetic)
            {
                var presenceColumn = Column(
                    spec.PresenceColumnName ?? PresenceColumnFor(spec.RelativePath).Value,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean)
                );
                presenceColumnsByRelativePath[spec.RelativePath] = presenceColumn;
                extensionColumns.Add(presenceColumn);

                extensionBindings.Add(
                    new WriteColumnBinding(
                        presenceColumn,
                        new WriteValueSource.Precomputed(),
                        presenceColumn.ColumnName.Value
                    )
                );
                presenceBindingIndicesByRelativePath[spec.RelativePath] = extensionBindings.Count - 1;
            }

            WriteValueSource memberSource = spec.SourceKind switch
            {
                KeyUnificationMemberSourceKind.Scalar => new WriteValueSource.Scalar(
                    Path(spec.RelativePath),
                    Int32Type()
                ),
                KeyUnificationMemberSourceKind.Descriptor => new WriteValueSource.DescriptorReference(
                    new QualifiedResourceName("Ed-Fi", "SampleDescriptor"),
                    Path(spec.RelativePath),
                    DescriptorValuePath: null
                ),
                _ => throw new InvalidOperationException($"Unsupported source kind '{spec.SourceKind}'."),
            };
            extensionBindings.Add(
                new WriteColumnBinding(memberColumn, memberSource, memberColumn.ColumnName.Value)
            );
        }

        var memberWritePlans = members
            .Select<KeyUnificationMemberSpec, KeyUnificationMemberWritePlan>(spec =>
            {
                var memberColumn = memberColumnsByRelativePath[spec.RelativePath];
                presenceColumnsByRelativePath.TryGetValue(spec.RelativePath, out var presenceColumn);
                presenceBindingIndicesByRelativePath.TryGetValue(
                    spec.RelativePath,
                    out var presenceBindingIndex
                );
                int? presenceBindingIndexOrNull = spec.PresenceSynthetic ? presenceBindingIndex : null;

                return spec.SourceKind switch
                {
                    KeyUnificationMemberSourceKind.Scalar => new KeyUnificationMemberWritePlan.ScalarMember(
                        MemberPathColumn: memberColumn.ColumnName,
                        RelativePath: Path(spec.RelativePath),
                        ScalarType: Int32Type(),
                        PresenceColumn: presenceColumn?.ColumnName,
                        PresenceBindingIndex: presenceBindingIndexOrNull,
                        PresenceIsSynthetic: spec.PresenceSynthetic
                    ),
                    KeyUnificationMemberSourceKind.Descriptor =>
                        new KeyUnificationMemberWritePlan.DescriptorMember(
                            MemberPathColumn: memberColumn.ColumnName,
                            RelativePath: Path(spec.RelativePath),
                            DescriptorResource: new QualifiedResourceName("Ed-Fi", "SampleDescriptor"),
                            PresenceColumn: presenceColumn?.ColumnName,
                            PresenceBindingIndex: presenceBindingIndexOrNull,
                            PresenceIsSynthetic: spec.PresenceSynthetic
                        ),
                    _ => throw new InvalidOperationException($"Unsupported source kind '{spec.SourceKind}'."),
                };
            })
            .ToList();

        var keyUnificationPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: canonicalBindingIndex,
            MembersInOrder: memberWritePlans
        );

        var extensionTableModel = new DbTableModel(
            Table: extensionTableName,
            JsonScope: Path(extensionJsonScope),
            Key: new TableKey(
                "PK_HostExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: extensionColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var extensionPlan = new TableWritePlan(
            TableModel: extensionTableModel,
            InsertSql: $"INSERT INTO {extensionSchema}.\"HostExtension\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, Math.Max(1, extensionBindings.Count), 65535),
            ColumnBindings: extensionBindings,
            KeyUnificationPlans: [keyUnificationPlan]
        );

        var plan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: [rootModel, extensionTableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, extensionPlan]
        );

        return (plan, canonicalBindingIndex, presenceBindingIndicesByRelativePath);
    }

    // ── Resolver-context builders ─────────────────────────────────────────

    /// <summary>
    /// Compositional per-member spec for <see cref="BuildRootPlanWithKeyUnificationMembers"/>.
    /// </summary>
    internal sealed record KeyUnificationMemberSpec(
        string RelativePath,
        KeyUnificationMemberSourceKind SourceKind,
        bool PresenceSynthetic,
        string? MemberColumnName = null,
        string? PresenceColumnName = null
    );

    internal enum KeyUnificationMemberSourceKind
    {
        Scalar,
        Descriptor,
    }

    /// <summary>
    /// Predictable naming helpers so tests can populate <c>CurrentRootRowByColumnName</c>
    /// with the same column names the multi-member builder uses.
    /// </summary>
    internal static DbColumnName MemberPathColumnFor(string relativePath) =>
        new("Member_" + SanitizePath(relativePath));

    internal static DbColumnName PresenceColumnFor(string relativePath) =>
        new("Presence_" + SanitizePath(relativePath));

    internal static DbColumnName CanonicalColumnFor() => new("KU_Canonical");

    private static string SanitizePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return "root";
        }
        return new string(relativePath.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    /// <summary>
    /// Build a root plan with a key-unification plan composed of one-or-more members.
    /// Bindings are: [0] = canonical (Precomputed), [1..N] = synthetic presence bindings
    /// (Precomputed) for each member that has <c>PresenceSynthetic</c> true, followed by
    /// per-member value bindings (Scalar for ScalarMember, DescriptorReference for
    /// DescriptorMember). Returns the plan along with the canonical binding index and a
    /// map from member relative path → synthetic-presence binding index (only for members
    /// that have a synthetic presence column).
    /// </summary>
    internal static (
        ResourceWritePlan Plan,
        int CanonicalBindingIndex,
        IReadOnlyDictionary<string, int> PresenceBindingIndicesByRelativePath
    ) BuildRootPlanWithKeyUnificationMembers(
        IReadOnlyList<KeyUnificationMemberSpec> members,
        bool canonicalIsNullable = true
    )
    {
        if (members is null || members.Count == 0)
        {
            throw new ArgumentException("At least one member spec is required.", nameof(members));
        }

        var canonicalColumn = Column(
            CanonicalColumnFor().Value,
            ColumnKind.Scalar,
            Int32Type(),
            isNullable: canonicalIsNullable
        );
        var columns = new List<DbColumnModel> { canonicalColumn };
        var bindings = new List<WriteColumnBinding>
        {
            new(canonicalColumn, new WriteValueSource.Precomputed(), canonicalColumn.ColumnName.Value),
        };
        var canonicalBindingIndex = 0;

        var presenceBindingIndicesByRelativePath = new Dictionary<string, int>(StringComparer.Ordinal);
        var memberBindingIndicesByRelativePath = new Dictionary<string, int>(StringComparer.Ordinal);
        var memberColumnsByRelativePath = new Dictionary<string, DbColumnModel>(StringComparer.Ordinal);
        var presenceColumnsByRelativePath = new Dictionary<string, DbColumnModel>(StringComparer.Ordinal);

        foreach (var spec in members)
        {
            var memberColumn = Column(
                spec.MemberColumnName ?? MemberPathColumnFor(spec.RelativePath).Value,
                ColumnKind.Scalar,
                spec.SourceKind == KeyUnificationMemberSourceKind.Scalar ? Int32Type() : Int64Type(),
                sourceJsonPath: Path(spec.RelativePath)
            );
            memberColumnsByRelativePath[spec.RelativePath] = memberColumn;
            columns.Add(memberColumn);

            if (spec.PresenceSynthetic)
            {
                var presenceColumn = Column(
                    spec.PresenceColumnName ?? PresenceColumnFor(spec.RelativePath).Value,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean)
                );
                presenceColumnsByRelativePath[spec.RelativePath] = presenceColumn;
                columns.Add(presenceColumn);

                var presenceBinding = new WriteColumnBinding(
                    presenceColumn,
                    new WriteValueSource.Precomputed(),
                    presenceColumn.ColumnName.Value
                );
                bindings.Add(presenceBinding);
                presenceBindingIndicesByRelativePath[spec.RelativePath] = bindings.Count - 1;
            }

            WriteValueSource memberSource = spec.SourceKind switch
            {
                KeyUnificationMemberSourceKind.Scalar => new WriteValueSource.Scalar(
                    Path(spec.RelativePath),
                    Int32Type()
                ),
                KeyUnificationMemberSourceKind.Descriptor => new WriteValueSource.DescriptorReference(
                    new QualifiedResourceName("Ed-Fi", "SampleDescriptor"),
                    Path(spec.RelativePath),
                    DescriptorValuePath: null
                ),
                _ => throw new InvalidOperationException($"Unsupported source kind '{spec.SourceKind}'."),
            };
            var memberBinding = new WriteColumnBinding(
                memberColumn,
                memberSource,
                memberColumn.ColumnName.Value
            );
            bindings.Add(memberBinding);
            memberBindingIndicesByRelativePath[spec.RelativePath] = bindings.Count - 1;
        }

        var rootModel = RootTable("Thing", columns);

        var memberWritePlans = members
            .Select<KeyUnificationMemberSpec, KeyUnificationMemberWritePlan>(spec =>
            {
                var memberColumn = memberColumnsByRelativePath[spec.RelativePath];
                presenceColumnsByRelativePath.TryGetValue(spec.RelativePath, out var presenceColumn);
                presenceBindingIndicesByRelativePath.TryGetValue(
                    spec.RelativePath,
                    out var presenceBindingIndex
                );
                int? presenceBindingIndexOrNull = spec.PresenceSynthetic ? presenceBindingIndex : null;

                return spec.SourceKind switch
                {
                    KeyUnificationMemberSourceKind.Scalar => new KeyUnificationMemberWritePlan.ScalarMember(
                        MemberPathColumn: memberColumn.ColumnName,
                        RelativePath: Path(spec.RelativePath),
                        ScalarType: Int32Type(),
                        PresenceColumn: presenceColumn?.ColumnName,
                        PresenceBindingIndex: presenceBindingIndexOrNull,
                        PresenceIsSynthetic: spec.PresenceSynthetic
                    ),
                    KeyUnificationMemberSourceKind.Descriptor =>
                        new KeyUnificationMemberWritePlan.DescriptorMember(
                            MemberPathColumn: memberColumn.ColumnName,
                            RelativePath: Path(spec.RelativePath),
                            DescriptorResource: new QualifiedResourceName("Ed-Fi", "SampleDescriptor"),
                            PresenceColumn: presenceColumn?.ColumnName,
                            PresenceBindingIndex: presenceBindingIndexOrNull,
                            PresenceIsSynthetic: spec.PresenceSynthetic
                        ),
                    _ => throw new InvalidOperationException($"Unsupported source kind '{spec.SourceKind}'."),
                };
            })
            .ToList();

        var keyUnificationPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: canonicalBindingIndex,
            MembersInOrder: memberWritePlans
        );
        var rootPlan = RootPlan(rootModel, bindings, keyUnificationPlans: [keyUnificationPlan]);
        return (
            WrapPlan(rootModel, [rootPlan], documentReferenceBindings: []),
            canonicalBindingIndex,
            presenceBindingIndicesByRelativePath
        );
    }

    /// <summary>
    /// Build a two-table plan whose separate table has a key-unification plan but is
    /// tagged with a configurable non-<see cref="DbTableKind.RootExtension"/> kind.
    /// </summary>
    internal static ResourceWritePlan BuildRootPlusSeparateTableWithKeyUnificationNonRootExtensionKind(
        string memberRelativePath = "$._ext.sample.memberA",
        DbTableKind nonRootExtensionKind = DbTableKind.CollectionExtensionScope
    )
    {
        var rootScalar = Column("FirstName", ColumnKind.Scalar, StringType());
        var rootModel = RootTable("Host", [rootScalar]);
        var rootBinding = new WriteColumnBinding(
            rootScalar,
            new WriteValueSource.Scalar(Path("$.firstName"), StringType()),
            "FirstName"
        );
        var rootPlan = RootPlan(rootModel, [rootBinding]);

        var extensionSchemaName = new DbSchemaName("sample");
        var parentKeyColumn = Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false);
        var canonicalColumn = Column(CanonicalColumnFor().Value, ColumnKind.Scalar, Int32Type());
        var memberColumn = Column(
            MemberPathColumnFor(memberRelativePath).Value,
            ColumnKind.Scalar,
            Int32Type(),
            sourceJsonPath: Path(memberRelativePath)
        );

        var bindings = new[]
        {
            new WriteColumnBinding(parentKeyColumn, new WriteValueSource.ParentKeyPart(0), "DocumentId"),
            new WriteColumnBinding(
                canonicalColumn,
                new WriteValueSource.Precomputed(),
                canonicalColumn.ColumnName.Value
            ),
            new WriteColumnBinding(
                memberColumn,
                new WriteValueSource.Scalar(Path(memberRelativePath), Int32Type()),
                memberColumn.ColumnName.Value
            ),
        };

        var keyUnificationPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: 1,
            MembersInOrder:
            [
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: memberColumn.ColumnName,
                    RelativePath: Path(memberRelativePath),
                    ScalarType: Int32Type(),
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                ),
            ]
        );

        var tableModel = new DbTableModel(
            Table: new DbTableName(extensionSchemaName, "HostExtension"),
            JsonScope: Path("$._ext.sample"),
            Key: new TableKey(
                "PK_HostExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [parentKeyColumn, canonicalColumn, memberColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: nonRootExtensionKind,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var extensionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO sample.\"HostExtension\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 3, 65535),
            ColumnBindings: bindings,
            KeyUnificationPlans: [keyUnificationPlan]
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: [rootModel, tableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, extensionPlan]
        );
    }

    internal static ProfileSeparateTableKeyUnificationContext BuildSeparateTableResolverContext(
        ResourceWritePlan writePlan,
        JsonNode? writableBody = null,
        RelationalWriteCurrentState? currentState = null,
        IReadOnlyDictionary<DbColumnName, object?>? currentRowByColumnName = null,
        FlatteningResolvedReferenceLookupSet? resolvedReferenceLookups = null,
        ProfileAppliedWriteRequest? profileRequest = null,
        ProfileAppliedWriteContext? profileAppliedContext = null
    ) =>
        new(
            WritableRequestBody: writableBody ?? new JsonObject(),
            CurrentState: currentState,
            CurrentRowByColumnName: currentRowByColumnName ?? new Dictionary<DbColumnName, object?>(),
            ResolvedReferenceLookups: resolvedReferenceLookups ?? EmptyResolvedReferenceLookups(writePlan),
            ProfileRequest: profileRequest ?? CreateRequest(),
            ProfileAppliedContext: profileAppliedContext
        );

    internal static ProfileRootKeyUnificationContext BuildResolverContext(
        ResourceWritePlan writePlan,
        JsonNode? writableBody = null,
        RelationalWriteCurrentState? currentState = null,
        IReadOnlyDictionary<DbColumnName, object?>? currentRootRowByColumnName = null,
        FlatteningResolvedReferenceLookupSet? resolvedReferenceLookups = null,
        ProfileAppliedWriteRequest? profileRequest = null,
        ProfileAppliedWriteContext? profileAppliedContext = null
    ) =>
        new(
            WritableRequestBody: writableBody ?? new JsonObject(),
            CurrentState: currentState,
            CurrentRootRowByColumnName: currentRootRowByColumnName ?? new Dictionary<DbColumnName, object?>(),
            ResolvedReferenceLookups: resolvedReferenceLookups ?? EmptyResolvedReferenceLookups(writePlan),
            ProfileRequest: profileRequest ?? CreateRequest(),
            ProfileAppliedContext: profileAppliedContext
        );

    internal static FlatteningResolvedReferenceLookupSet EmptyResolvedReferenceLookups(
        ResourceWritePlan writePlan
    ) => FlatteningResolvedReferenceLookupSet.Create(writePlan, EmptyResolvedReferenceSet());

    internal static ResolvedReferenceSet EmptyResolvedReferenceSet() =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );

    // ── Synthesizer builders and stubs ─────────────────────────────────────

    /// <summary>
    /// Build a <see cref="RelationalWriteProfileMergeSynthesizer"/> composed with real
    /// classifier and resolver instances. This is the end-to-end entry point for tests
    /// that verify the synthesizer's composition behavior.
    /// </summary>
    internal static RelationalWriteProfileMergeSynthesizer BuildProfileSynthesizer() =>
        new(
            new ProfileRootTableBindingClassifier(),
            new ProfileRootKeyUnificationResolver(),
            new ProfileSeparateTableBindingClassifier(),
            new ProfileSeparateTableKeyUnificationResolver(),
            new ProfileSeparateTableMergeDecider()
        );

    /// <summary>
    /// Unwrap a synthesis outcome to its underlying <see cref="RelationalWriteMergeResult"/>,
    /// failing the test if the outcome was a rejection.
    /// </summary>
    internal static RelationalWriteMergeResult UnwrapMergeResult(ProfileMergeOutcome outcome) =>
        outcome.MergeResult
        ?? throw new InvalidOperationException(
            "Expected success outcome from profile merge synthesizer, got rejection: "
                + (outcome.CreatabilityRejection?.Message ?? "<unknown>")
        );

    /// <summary>
    /// Build a minimal <see cref="FlattenedWriteSet"/> whose root row has literal values
    /// in binding-index order, one per supplied value. Bindings not covered are <c>null</c>
    /// literals.
    /// </summary>
    internal static FlattenedWriteSet BuildFlattenedWriteSetFrom(
        ResourceWritePlan writePlan,
        params object?[] literalValuesByBindingIndex
    )
    {
        var rootPlan = writePlan.TablePlansInDependencyOrder[0];
        var bindings = rootPlan.ColumnBindings;
        var values = new FlattenedWriteValue[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(
                i < literalValuesByBindingIndex.Length ? literalValuesByBindingIndex[i] : null
            );
        }
        return new FlattenedWriteSet(new RootWriteRowBuffer(rootPlan, values));
    }

    /// <summary>
    /// Build a <see cref="RelationalWriteCurrentState"/> with a single root-table row
    /// (and no other tables). Column-ordinal order is the root table's
    /// <see cref="DbTableModel.Columns"/> order.
    /// </summary>
    internal static RelationalWriteCurrentState BuildCurrentStateWithSingleRootRow(
        ResourceWritePlan writePlan,
        params object?[] columnValues
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: 345L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero)
            ),
            [new HydratedTableRows(writePlan.TablePlansInDependencyOrder[0].TableModel, [columnValues])],
            []
        );

    /// <summary>
    /// Build a <see cref="RelationalWriteCurrentState"/> for a two-table plan: one row on
    /// the root table and optionally one row on the root-extension table. Pass
    /// <paramref name="extensionRowValues"/> = <c>null</c> for the "no stored extension row"
    /// case (the extension table appears in <c>TableRowsInDependencyOrder</c> with an empty
    /// rows list so the synthesizer's lookup correctly reports "no stored row").
    /// </summary>
    internal static RelationalWriteCurrentState BuildCurrentStateWithRootAndExtensionRow(
        ResourceWritePlan writePlan,
        object?[] rootRowValues,
        object?[]? extensionRowValues
    )
    {
        var rootTableModel = writePlan.TablePlansInDependencyOrder[0].TableModel;
        var extensionTableModel = writePlan.TablePlansInDependencyOrder[1].TableModel;
        var extensionRows = extensionRowValues is null ? (IReadOnlyList<object?[]>)[] : [extensionRowValues];
        return new(
            new DocumentMetadataRow(
                DocumentId: 345L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(rootTableModel, [rootRowValues]),
                new HydratedTableRows(extensionTableModel, extensionRows),
            ],
            []
        );
    }

    /// <summary>
    /// Build a <see cref="FlattenedWriteSet"/> whose root row has literal values in
    /// binding-index order and carries a single <see cref="RootExtensionWriteRowBuffer"/>
    /// for the supplied <paramref name="extensionTablePlan"/> with its own literal values.
    /// </summary>
    internal static FlattenedWriteSet BuildFlattenedWriteSetWithExtensionRow(
        ResourceWritePlan writePlan,
        TableWritePlan extensionTablePlan,
        object?[] rootLiteralsByBindingIndex,
        object?[] extensionLiteralsByBindingIndex
    )
    {
        var rootPlan = writePlan.TablePlansInDependencyOrder[0];
        var rootBindings = rootPlan.ColumnBindings;
        var rootValues = new FlattenedWriteValue[rootBindings.Length];
        for (var i = 0; i < rootBindings.Length; i++)
        {
            rootValues[i] = new FlattenedWriteValue.Literal(
                i < rootLiteralsByBindingIndex.Length ? rootLiteralsByBindingIndex[i] : null
            );
        }

        var extensionBindings = extensionTablePlan.ColumnBindings;
        var extensionValues = new FlattenedWriteValue[extensionBindings.Length];
        for (var i = 0; i < extensionBindings.Length; i++)
        {
            extensionValues[i] = new FlattenedWriteValue.Literal(
                i < extensionLiteralsByBindingIndex.Length ? extensionLiteralsByBindingIndex[i] : null
            );
        }

        var extensionRow = new RootExtensionWriteRowBuffer(extensionTablePlan, extensionValues);
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, rootExtensionRows: [extensionRow])
        );
    }

    /// <summary>
    /// Pre-canned classifier used by invariant tests that need to force a specific
    /// binding disposition without going through real scope matching.
    /// </summary>
    internal sealed class StubClassifier(ProfileRootTableBindingClassification classification)
        : IProfileRootTableBindingClassifier
    {
        public ProfileRootTableBindingClassification Classify(
            ResourceWritePlan writePlan,
            ProfileAppliedWriteRequest profileRequest,
            ProfileAppliedWriteContext? profileAppliedContext
        ) => classification;
    }

    /// <summary>
    /// No-op resolver used alongside <see cref="StubClassifier"/> when a test wants to
    /// isolate the synthesizer's overlay behavior.
    /// </summary>
    internal sealed class NoOpResolver : IProfileRootKeyUnificationResolver
    {
        public void Resolve(
            TableWritePlan rootTableWritePlan,
            ProfileRootKeyUnificationContext context,
            FlattenedWriteValue[] mergedRowValuesMutable,
            ImmutableHashSet<int> resolverOwnedBindingIndices
        )
        {
            // Intentionally does nothing; tests that use this stub verify overlay-only paths.
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static RelationalScalarType StringType() => new(ScalarKind.String, MaxLength: 50);

    private static RelationalScalarType Int32Type() => new(ScalarKind.Int32);

    private static RelationalScalarType Int64Type() => new(ScalarKind.Int64);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    /// <summary>
    /// Optionally strips <paramref name="scopeAddress"/> off the front of
    /// <paramref name="absoluteRelativePath"/> to produce a scope-relative path that matches
    /// production <see cref="WritePlanJsonPathConventions.DeriveScopeRelativePath"/> output.
    /// When <paramref name="useScopeRelative"/> is false, the path is returned unchanged (the
    /// historical test-double behaviour where "relative" paths are actually absolute).
    /// </summary>
    private static string ConformBindingRelativePath(
        string absoluteRelativePath,
        string scopeAddress,
        bool useScopeRelative
    )
    {
        if (!useScopeRelative)
        {
            return absoluteRelativePath;
        }
        if (string.Equals(scopeAddress, "$", StringComparison.Ordinal))
        {
            // Root scope: absolute and scope-relative forms coincide.
            return absoluteRelativePath;
        }
        if (string.Equals(absoluteRelativePath, scopeAddress, StringComparison.Ordinal))
        {
            return "$";
        }
        if (
            absoluteRelativePath.StartsWith(scopeAddress, StringComparison.Ordinal)
            && absoluteRelativePath.Length > scopeAddress.Length
            && absoluteRelativePath[scopeAddress.Length] == '.'
        )
        {
            // Drop the scope prefix and replace with "$" so the path remains canonical.
            return "$" + absoluteRelativePath[scopeAddress.Length..];
        }
        throw new ArgumentException(
            $"Binding relative path '{absoluteRelativePath}' is not a descendant of scope '{scopeAddress}'.",
            nameof(absoluteRelativePath)
        );
    }

    private static DbColumnModel Column(
        string columnName,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        JsonPathExpression? sourceJsonPath = null,
        bool isNullable = true
    ) =>
        new(
            ColumnName: new DbColumnName(columnName),
            Kind: kind,
            ScalarType: scalarType,
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null,
            Storage: new ColumnStorage.Stored()
        );

    private static DbTableModel RootTable(string name, IReadOnlyList<DbColumnModel> columns) =>
        new(
            Table: new DbTableName(_defaultSchema, name),
            JsonScope: Path("$"),
            Key: new TableKey(
                "PK_" + name,
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

    private static TableWritePlan RootPlan(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindings,
        IReadOnlyList<KeyUnificationWritePlan>? keyUnificationPlans = null
    ) =>
        new(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO {tableModel.Table.Schema.Value}.\"{tableModel.Table.Name}\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, Math.Max(1, bindings.Count), 65535),
            ColumnBindings: bindings,
            KeyUnificationPlans: keyUnificationPlans ?? []
        );

    private static ResourceWritePlan WrapPlan(
        DbTableModel rootModel,
        IReadOnlyList<TableWritePlan> tablePlans,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    ) =>
        new(
            new RelationalResourceModel(
                Resource: _defaultResource,
                PhysicalSchema: _defaultSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootModel,
                TablesInDependencyOrder: tablePlans.Select(p => p.TableModel).ToList(),
                DocumentReferenceBindings: documentReferenceBindings,
                DescriptorEdgeSources: []
            ),
            tablePlans
        );
}
