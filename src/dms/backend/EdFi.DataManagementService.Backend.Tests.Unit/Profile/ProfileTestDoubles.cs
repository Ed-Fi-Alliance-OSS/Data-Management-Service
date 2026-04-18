// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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

    // ── Helpers ────────────────────────────────────────────────────────────

    private static RelationalScalarType StringType() => new(ScalarKind.String, MaxLength: 50);

    private static RelationalScalarType Int32Type() => new(ScalarKind.Int32);

    private static RelationalScalarType Int64Type() => new(ScalarKind.Int64);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static DbColumnModel Column(
        string columnName,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        JsonPathExpression? sourceJsonPath = null
    ) =>
        new(
            ColumnName: new DbColumnName(columnName),
            Kind: kind,
            ScalarType: scalarType,
            IsNullable: true,
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
