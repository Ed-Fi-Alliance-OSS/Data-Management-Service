// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Focused fixture for link injection in <see cref="DocumentReconstituter.ReconstitutePage"/>.
/// Builds a minimal one-table read plan with a single document reference and exercises the
/// resolver-aware overload.
/// </summary>
[TestFixture]
public class Given_DocumentReconstituter_With_Document_Reference_Link_Injection
{
    private const short SchoolResourceKeyId = 7;
    private const long SchoolDocumentId = 901L;

    private static readonly Guid SchoolDocumentUuid = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "StudentSchoolAssociation");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly DbTableName _rootTableName = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );

    private static readonly JsonPathExpression _rootScope = new("$", []);
    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );
    private static readonly JsonPathExpression _schoolReferenceSchoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    private static readonly DocumentLinkSlugTriple _expectedSlug = new(
        ProjectEndpointName: "ed-fi",
        EndpointName: "schools",
        ResourceName: "School"
    );

    [Test]
    public void It_emits_link_rel_and_href_for_a_concrete_reference_when_FK_hits_the_lookup_map()
    {
        var resolver = new StubSlugResolver(_expectedSlug);
        var result = ReconstituteSingleDocument(
            schoolDocumentIdFk: SchoolDocumentId,
            lookupRows: [(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId)],
            resolver: resolver
        );

        var link = result["schoolReference"]!["link"].Should().BeOfType<JsonObject>().Subject;
        link["rel"]!.GetValue<string>().Should().Be("School");
        link["href"]!.GetValue<string>().Should().Be($"/ed-fi/schools/{SchoolDocumentUuid.ToString("D")}");
        resolver.Calls.Should().ContainSingle();
        resolver.Calls[0].Should().Be((SchoolResourceKeyId));
    }

    [Test]
    public void It_emits_href_with_36_char_lowercase_hex_uuid_with_hyphens_at_8_13_18_23()
    {
        var resolver = new StubSlugResolver(_expectedSlug);
        var result = ReconstituteSingleDocument(
            schoolDocumentIdFk: SchoolDocumentId,
            lookupRows: [(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId)],
            resolver: resolver
        );

        var href = result["schoolReference"]!["link"]!["href"]!.GetValue<string>();
        var uuid = href[(href.LastIndexOf('/') + 1)..];

        uuid.Length.Should().Be(36);
        uuid[8].Should().Be('-');
        uuid[13].Should().Be('-');
        uuid[18].Should().Be('-');
        uuid[23].Should().Be('-');
        uuid.Where(c => c != '-').Should().OnlyContain(c => char.IsAsciiHexDigitLower(c) || char.IsDigit(c));
    }

    [Test]
    public void It_does_not_emit_link_when_FK_value_is_null()
    {
        var resolver = new StubSlugResolver(_expectedSlug);
        var result = ReconstituteSingleDocument(
            schoolDocumentIdFk: null,
            lookupRows: [(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId)],
            resolver: resolver
        );

        // Identity-field reconstitution still emits schoolReference even with a null FK, but
        // link injection requires a non-null FK to look up the page-scoped map.
        result["schoolReference"]!["schoolId"]!.GetValue<int>().Should().Be(255901);
        result["schoolReference"]!["link"].Should().BeNull();
        resolver.Calls.Should().BeEmpty();
    }

    // Parameterized over the binding's TargetResource shape (concrete School vs abstract
    // EducationOrganization) to satisfy the discrete acceptance criterion in
    // `06a-link-injection-implementation.md` §Unit tests: "concrete reference with non-null FK
    // but auxiliary-lookup miss (no link)" + "abstract reference with auxiliary-lookup miss (no
    // link)". The miss-suppression gate inside EmitReferenceLink fires before the binding's
    // TargetResource is consulted, so both shapes structurally exercise the same code path —
    // the parameterization formalizes the coverage rather than introducing a new branch.
    [TestCase("Ed-Fi", "School", TestName = "concrete_reference_FK_present_lookup_miss")]
    [TestCase("Ed-Fi", "EducationOrganization", TestName = "abstract_reference_FK_present_lookup_miss")]
    public void It_does_not_emit_link_when_FK_is_present_but_lookup_map_misses(
        string targetProjectName,
        string targetResourceName
    )
    {
        var resolver = new StubSlugResolver(_expectedSlug);
        var result = ReconstituteSingleDocument(
            schoolDocumentIdFk: SchoolDocumentId,
            lookupRows: [], // no rows: lookup miss
            resolver: resolver,
            targetResource: new QualifiedResourceName(targetProjectName, targetResourceName)
        );

        result["schoolReference"]!["schoolId"]!.GetValue<int>().Should().Be(255901);
        result["schoolReference"]!["link"].Should().BeNull();
        resolver.Calls.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_emit_link_when_using_the_no_link_overload()
    {
        // Sanity: the legacy ReconstitutePage(readPlan, hydratedPage) overload never emits link.
        var readPlan = BuildReadPlan();
        var hydratedPage = BuildHydratedPage(
            schoolDocumentIdFk: SchoolDocumentId,
            lookupRows: [(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId)]
        );

        var results = DocumentReconstituter.ReconstitutePage(readPlan, hydratedPage);

        results.Should().ContainSingle();
        results[0]!["schoolReference"]!["link"].Should().BeNull();
    }

    [Test]
    public void It_propagates_exceptions_from_the_resolver_unchanged()
    {
        var resolver = new ThrowingSlugResolver(new InvalidOperationException("boom: unknown ResourceKeyId"));
        var readPlan = BuildReadPlan();
        var hydratedPage = BuildHydratedPage(
            schoolDocumentIdFk: SchoolDocumentId,
            lookupRows: [(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId)]
        );

        Action act = () =>
            DocumentReconstituter.ReconstitutePage(readPlan, hydratedPage, BuildMappingSet(), resolver);

        act.Should().Throw<InvalidOperationException>().WithMessage("boom*");
    }

    [Test]
    public void It_resolves_concrete_subclass_for_abstract_reference_via_ResourceKeyId()
    {
        // The lookup row pins ResourceKeyId to the concrete subclass; the resolver must
        // see that exact id even though the binding's TargetResource is the abstract type.
        const short concreteSubclassResourceKeyId = 42;
        var concreteSlug = new DocumentLinkSlugTriple(
            ProjectEndpointName: "ed-fi",
            EndpointName: "schools",
            ResourceName: "School"
        );
        var resolver = new StubSlugResolver(concreteSlug);

        var result = ReconstituteSingleDocument(
            schoolDocumentIdFk: SchoolDocumentId,
            lookupRows: [(SchoolDocumentId, SchoolDocumentUuid, concreteSubclassResourceKeyId)],
            resolver: resolver
        );

        resolver.Calls.Should().ContainSingle().Which.Should().Be(concreteSubclassResourceKeyId);
        result["schoolReference"]!["link"]!["rel"]!.GetValue<string>().Should().Be("School");
    }

    [Test]
    public void It_resolves_two_references_targeting_the_same_DocumentId_via_a_single_lookup_row()
    {
        // Page-level dedup: the auxiliary lookup returns ONE row for the shared school,
        // and both documents on the page emit a link pointing at it. Confirms the
        // page-scoped map is consulted per-reference-site, not per-row.
        var resolver = new StubSlugResolver(_expectedSlug);
        var readPlan = BuildReadPlan();
        var hydratedPage = BuildHydratedPageWithTwoDocumentsSharingSchool();

        var results = DocumentReconstituter.ReconstitutePage(
            readPlan,
            hydratedPage,
            BuildMappingSet(),
            resolver
        );

        results.Should().HaveCount(2);
        string expectedHref = $"/ed-fi/schools/{SchoolDocumentUuid.ToString("D")}";
        foreach (var document in results)
        {
            var link = document["schoolReference"]!["link"]!.AsObject();
            link["rel"]!.GetValue<string>().Should().Be("School");
            link["href"]!.GetValue<string>().Should().Be(expectedHref);
        }

        // Resolver is called per emission site (2 sites, 1 ResourceKeyId), proving the page-
        // scoped lookup map serves both reference sites from the same single row.
        resolver.Calls.Should().HaveCount(2);
        resolver.Calls.Should().AllBeEquivalentTo(SchoolResourceKeyId);
    }

    private static HydratedPage BuildHydratedPageWithTwoDocumentsSharingSchool()
    {
        var rootTableModel = BuildRootTableModel();
        object?[] firstRow = [1L, (object?)SchoolDocumentId, 255901];
        object?[] secondRow = [2L, (object?)SchoolDocumentId, 255902];

        var metadataRows = new[]
        {
            new DocumentMetadataRow(
                DocumentId: 1L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: DateTimeOffset.UnixEpoch,
                IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
            ),
            new DocumentMetadataRow(
                DocumentId: 2L,
                DocumentUuid: Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: DateTimeOffset.UnixEpoch,
                IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
            ),
        };

        var lookup = new HydratedDocumentReferenceLookup([
            new DocumentReferenceLookupRow(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId),
        ]);

        return new HydratedPage(
            TotalCount: null,
            DocumentMetadata: metadataRows,
            TableRowsInDependencyOrder: [new HydratedTableRows(rootTableModel, [firstRow, secondRow])],
            DescriptorRowsInPlanOrder: []
        )
        {
            DocumentReferenceLookup = lookup,
        };
    }

    private static JsonNode ReconstituteSingleDocument(
        long? schoolDocumentIdFk,
        IReadOnlyList<(long DocumentId, Guid DocumentUuid, short ResourceKeyId)> lookupRows,
        IDocumentLinkSlugResolver resolver,
        QualifiedResourceName? targetResource = null
    )
    {
        var readPlan = BuildReadPlan(targetResource ?? _schoolResource);
        var hydratedPage = BuildHydratedPage(schoolDocumentIdFk, lookupRows);

        var results = DocumentReconstituter.ReconstitutePage(
            readPlan,
            hydratedPage,
            BuildMappingSet(),
            resolver
        );

        results.Should().ContainSingle();
        return results[0];
    }

    private static ResourceReadPlan BuildReadPlan() => BuildReadPlan(_schoolResource);

    private static ResourceReadPlan BuildReadPlan(QualifiedResourceName targetResource)
    {
        var rootTable = BuildRootTableModel();
        var model = new RelationalResourceModel(
            Resource: _resource,
            PhysicalSchema: rootTable.Table.Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: targetResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: _schoolReferenceSchoolIdPath,
                            ReferenceJsonPath: _schoolReferenceSchoolIdPath,
                            Column: new DbColumnName("SchoolReference_SchoolId")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );

        var refProjection = new ReferenceIdentityProjectionTablePlan(
            Table: rootTable.Table,
            BindingsInOrder:
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    TargetResource: targetResource,
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(
                            ReferenceJsonPath: _schoolReferenceSchoolIdPath,
                            ColumnOrdinal: 2,
                            ScalarType: new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                ),
            ]
        );

        var lookupPlan = new DocumentReferenceLookupPlan(
            SelectByKeysetSql: "SELECT 1;",
            ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: new KeysetTableContract(
                Table: new SqlRelationRef.TempTable("page"),
                DocumentIdColumnName: new DbColumnName("DocumentId")
            ),
            TablePlansInDependencyOrder: [new TableReadPlan(rootTable, "SELECT 1;")],
            ReferenceIdentityProjectionPlansInDependencyOrder: [refProjection],
            DescriptorProjectionPlansInOrder: [],
            DocumentReferenceLookup: lookupPlan
        );
    }

    private static DbTableModel BuildRootTableModel() =>
        new(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: _schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolReference_SchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: _schoolReferenceSchoolIdPath,
                    TargetResource: null
                ),
            ],
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

    private static HydratedPage BuildHydratedPage(
        long? schoolDocumentIdFk,
        IReadOnlyList<(long DocumentId, Guid DocumentUuid, short ResourceKeyId)> lookupRows
    )
    {
        var rootTableModel = BuildRootTableModel();
        object?[] row = [1L, (object?)schoolDocumentIdFk, 255901];

        var metadataRow = new DocumentMetadataRow(
            DocumentId: 1L,
            DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: DateTimeOffset.UnixEpoch,
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );

        var lookup = new HydratedDocumentReferenceLookup([
            .. lookupRows.Select(r => new DocumentReferenceLookupRow(
                r.DocumentId,
                r.DocumentUuid,
                r.ResourceKeyId
            )),
        ]);

        return new HydratedPage(
            TotalCount: null,
            DocumentMetadata: [metadataRow],
            TableRowsInDependencyOrder: [new HydratedTableRows(rootTableModel, [row])],
            DescriptorRowsInPlanOrder: []
        )
        {
            DocumentReferenceLookup = lookup,
        };
    }

    private static MappingSet BuildMappingSet()
    {
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "rmv-test",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(),
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(
                effectiveSchema.EffectiveSchemaHash,
                SqlDialect.Pgsql,
                effectiveSchema.RelationalMappingVersion
            ),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: effectiveSchema,
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private sealed class StubSlugResolver : IDocumentLinkSlugResolver
    {
        private readonly DocumentLinkSlugTriple _slug;

        public StubSlugResolver(DocumentLinkSlugTriple slug)
        {
            _slug = slug;
        }

        public List<short> Calls { get; } = [];

        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId)
        {
            Calls.Add(resourceKeyId);
            return _slug;
        }
    }

    private sealed class ThrowingSlugResolver(Exception exception) : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) => throw exception;
    }
}
