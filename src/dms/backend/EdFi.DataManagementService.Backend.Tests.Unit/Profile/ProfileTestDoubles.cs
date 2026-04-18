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
        new(new ProfileRootTableBindingClassifier(), new ProfileRootKeyUnificationResolver());

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
