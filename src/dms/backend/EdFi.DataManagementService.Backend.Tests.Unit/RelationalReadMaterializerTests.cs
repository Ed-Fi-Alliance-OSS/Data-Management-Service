// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalReadMaterializer
{
    private static readonly Guid _documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private static readonly Guid _secondDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc");

    [Test]
    public void It_injects_a_canonical_etag_and_stored_last_modified_date_for_external_response_reads()
    {
        var sut = CreateMaterializer();
        var readPlan = CreateReadPlan();

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 9, 10, 11, TimeSpan.FromHours(-5))
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );

        result.Should().BeOfType<JsonObject>();
        result["name"]!.GetValue<string>().Should().Be("Lincoln High");
        result["id"]!.GetValue<string>().Should().Be(_documentUuid.ToString());
        // ContentVersion 91; schemaEpoch = first 8 hex of BuildMappingSet()'s EffectiveSchemaHash
        // ("01234567"); format j (JSON); no profile ("_"); links enabled by default ("l").
        result["_etag"]!.GetValue<string>().Should().Be("91-01234567.j._.l");
        result["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-04-03T14:10:11Z");
    }

    [Test]
    public void It_derives_the_external_etag_from_content_version_and_leaves_change_version_absent()
    {
        var sut = CreateMaterializer();
        var readPlan = CreateReadPlan();

        var firstResult = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );
        var secondResult = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 907L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );

        // The composed etag embeds ContentVersion (see RelationalReadMaterializer.ComposeEtag), so
        // distinct content versions must yield distinct etags — the inverse of the pre-ContentVersion
        // content-hash contract this test used to pin.
        firstResult["_etag"]!
            .GetValue<string>()
            .Should()
            .NotBe(
                secondResult["_etag"]!.GetValue<string>(),
                "the composed etag embeds ContentVersion, so distinct content versions must yield distinct etags"
            );
        firstResult["ChangeVersion"].Should().BeNull();
        secondResult["ChangeVersion"].Should().BeNull();
    }

    [Test]
    public void It_leaves_api_metadata_out_of_stored_document_reads()
    {
        var sut = CreateMaterializer();
        var readPlan = CreateReadPlan();

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().BeOfType<JsonObject>();
        result["name"]!.GetValue<string>().Should().Be("Lincoln High");
        result["id"].Should().BeNull();
        result["_etag"].Should().BeNull();
        result["_lastModifiedDate"].Should().BeNull();
    }

    [Test]
    public void It_projects_descriptor_uris_from_hydrated_descriptor_rows()
    {
        var sut = CreateMaterializer();
        var readPlan = CreateReadPlanWithDescriptor();

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedDescriptorTableRows(readPlan, (345L, 601L)),
                CreateHydratedDescriptorRows((601L, "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade")),
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().BeOfType<JsonObject>();
        result["entryGradeLevelDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be("uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade");
    }

    [Test]
    public void It_materializes_page_documents_in_metadata_order_for_external_response_reads()
    {
        var sut = CreateMaterializer();
        var readPlan = CreateReadPlan();
        var firstDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 345L,
            documentUuid: _documentUuid,
            contentVersion: 91L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
        );
        var secondDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 678L,
            documentUuid: _secondDocumentUuid,
            contentVersion: 92L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 4, 15, 11, 12, TimeSpan.Zero)
        );

        var result = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                CreateHydratedPage(
                    [firstDocumentMetadata, secondDocumentMetadata],
                    CreateHydratedTableRows(readPlan, (678L, "Cedar High"), (345L, "Lincoln High")),
                    []
                ),
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );

        result.Should().HaveCount(2);
        result.Select(static document => document.DocumentMetadata.DocumentId).Should().Equal(345L, 678L);
        result
            .Select(document => document.Document["name"]!.GetValue<string>())
            .Should()
            .Equal("Lincoln High", "Cedar High");
        result
            .Select(document => document.Document["id"]!.GetValue<string>())
            .Should()
            .Equal(_documentUuid.ToString(), _secondDocumentUuid.ToString());
        result
            .Select(document => document.Document["_lastModifiedDate"]!.GetValue<string>())
            .Should()
            .Equal("2026-04-03T14:10:11Z", "2026-04-04T15:11:12Z");
    }

    [Test]
    public void It_materializes_page_documents_from_shared_descriptor_rows_for_stored_document_reads()
    {
        var sut = CreateMaterializer();
        var readPlan = CreateReadPlanWithDescriptor();
        var firstDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 345L,
            documentUuid: _documentUuid,
            contentVersion: 91L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
        );
        var secondDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 678L,
            documentUuid: _secondDocumentUuid,
            contentVersion: 92L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 4, 15, 11, 12, TimeSpan.Zero)
        );

        var result = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                CreateHydratedPage(
                    [firstDocumentMetadata, secondDocumentMetadata],
                    CreateHydratedDescriptorTableRows(readPlan, (678L, 601L), (345L, 601L)),
                    CreateHydratedDescriptorRows(
                        (601L, "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade")
                    )
                ),
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().HaveCount(2);
        result.Select(static document => document.DocumentMetadata.DocumentId).Should().Equal(345L, 678L);
        result
            .Select(document => document.Document["entryGradeLevelDescriptor"]!.GetValue<string>())
            .Should()
            .Equal(
                "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade",
                "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"
            );

        foreach (var document in result)
        {
            document.Document["id"].Should().BeNull();
            document.Document["_etag"].Should().BeNull();
            document.Document["_lastModifiedDate"].Should().BeNull();
        }
    }

    private static RelationalReadMaterializer CreateMaterializer(ResourceLinksOptions? linksOptions = null) =>
        new(
            new NoLinkSlugResolver(),
            Microsoft.Extensions.Options.Options.Create(linksOptions ?? new ResourceLinksOptions()),
            new EtagComposer()
        );

    // ExternalResponse materialization now requires both EtagVariant and MappingSet to compose the
    // _etag (see RelationalReadMaterializer.ComposeEtag). The read plans in this fixture have no
    // DocumentReferenceBindings, so supplying a MappingSet never routes through slug resolution —
    // NoLinkSlugResolver above is safe to keep as the resolver stand-in.
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

    /// <summary>
    /// Stand-in resolver for tests that never exercise link emission: the existing
    /// <see cref="RelationalReadMaterializerTests"/> assertions all build requests without a
    /// <c>MappingSet</c>, so the materializer takes the no-link reconstitution path and
    /// never calls <see cref="IDocumentLinkSlugResolver.Resolve"/>.
    /// </summary>
    private sealed class NoLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
            throw new InvalidOperationException(
                "NoLinkSlugResolver should not be invoked for legacy (no-MappingSet) materializer requests."
            );
    }

    private static ResourceReadPlan CreateReadPlan()
    {
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new TableReadPlan(rootTable, "select \"DocumentId\", \"Name\" from edfi.\"School\"")],
            [],
            []
        );
    }

    private static ResourceReadPlan CreateReadPlanWithDescriptor()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");
        var descriptorValuePath = new JsonPathExpression(
            "$.entryGradeLevelDescriptor",
            [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
        );
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    descriptorResource
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new ResourceReadPlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "School"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootTable,
                TablesInDependencyOrder: [rootTable],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: descriptorValuePath,
                        Table: rootTable.Table,
                        FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                        DescriptorResource: descriptorResource
                    ),
                ]
            ),
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [
                new TableReadPlan(
                    rootTable,
                    "select \"DocumentId\", \"EntryGradeLevelDescriptor_DescriptorId\" from edfi.\"School\""
                ),
            ],
            [],
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "select \"DescriptorId\", \"Uri\" from dms.\"Descriptor\"",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSource(
                            DescriptorValuePath: descriptorValuePath,
                            Table: rootTable.Table,
                            DescriptorResource: descriptorResource,
                            DescriptorIdColumnOrdinal: 1
                        ),
                    ]
                ),
            ]
        );
    }

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        long contentVersion,
        DateTimeOffset contentLastModifiedAt
    ) => CreateDocumentMetadataRow(345L, _documentUuid, contentVersion, contentLastModifiedAt);

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        long documentId,
        Guid documentUuid,
        long contentVersion,
        DateTimeOffset contentLastModifiedAt
    )
    {
        return new DocumentMetadataRow(
            DocumentId: documentId,
            DocumentUuid: documentUuid,
            ContentVersion: contentVersion,
            IdentityVersion: 92L,
            ContentLastModifiedAt: contentLastModifiedAt,
            IdentityLastModifiedAt: contentLastModifiedAt
        );
    }

    private static IReadOnlyList<HydratedTableRows> CreateHydratedTableRows(
        ResourceReadPlan readPlan,
        params (long DocumentId, string Name)[] rows
    )
    {
        return
        [
            new HydratedTableRows(
                readPlan.Model.Root,
                rows.Select(row => new object?[] { row.DocumentId, row.Name }).ToArray()
            ),
        ];
    }

    private static IReadOnlyList<HydratedTableRows> CreateHydratedDescriptorTableRows(
        ResourceReadPlan readPlan,
        params (long DocumentId, long DescriptorId)[] rows
    )
    {
        return
        [
            new HydratedTableRows(
                readPlan.Model.Root,
                rows.Select(row => new object?[] { row.DocumentId, row.DescriptorId }).ToArray()
            ),
        ];
    }

    private static IReadOnlyList<HydratedDescriptorRows> CreateHydratedDescriptorRows(
        params (long DescriptorId, string Uri)[] rows
    )
    {
        return
        [
            new HydratedDescriptorRows(
                rows.Select(row => new DescriptorUriRow(row.DescriptorId, row.Uri)).ToArray()
            ),
        ];
    }

    private static HydratedPage CreateHydratedPage(
        IReadOnlyList<DocumentMetadataRow> documentMetadata,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<HydratedDescriptorRows> descriptorRowsInPlanOrder
    ) => new(null, documentMetadata, tableRowsInDependencyOrder, descriptorRowsInPlanOrder);
}

/// <summary>
/// Verifies link emission at the materializer boundary: the reconstituted intermediate is
/// always link-bearing (caller-agnostic) regardless of <see cref="ResourceLinksOptions.Enabled"/>.
/// The served <c>_etag</c> is composed as <c>"{ContentVersion}-{variantKey}"</c> (see
/// <see cref="RelationalReadMaterializer"/>'s <c>ComposeEtag</c>), where the variant key's link
/// flag reflects <see cref="ResourceLinksOptions.Enabled"/> — so unlike the legacy content-hash
/// etag, the composed etag is deliberately link-mode-sensitive, not link-decoration-independent
/// (see <c>design-docs/link-injection.md</c> §Cache and Etag, clarified by DMS-1005). The
/// <see cref="ResourceLinksOptions.Enabled"/> flag-off strip pass is exercised separately via
/// <see cref="IRelationalReadMaterializer.StripReferenceLinks"/> because the materializer no
/// longer applies it as part of <c>Materialize</c> — the repository wrapper drives the strip
/// after readable-profile projection.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_RelationalReadMaterializer_With_Link_Injection_And_External_Response
{
    private const short SchoolResourceKeyId = 7;
    private const long SchoolDocumentId = 901L;

    private static readonly Guid AcademicWeekDocumentUuid = Guid.Parse(
        "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"
    );
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

    private static readonly DocumentLinkSlugTriple _schoolSlug = new(
        ProjectEndpointName: "ed-fi",
        EndpointName: "schools",
        ResourceName: "School"
    );

    [Test]
    public void It_emits_link_bearing_intermediate_with_self_consistent_etag()
    {
        var sut = CreateMaterializer(new ResourceLinksOptions { Enabled = true });
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();

        var result = MaterializeSingleExternalResponse(sut, readPlan);

        // The reconstituted intermediate is always link-bearing (caller-agnostic) regardless
        // of ResourceLinksOptions.Enabled — the flag is honored by the response-boundary
        // strip pass in the repository wrapper, not inside the materializer.
        result["schoolReference"]!
            ["link"]
            .Should()
            .NotBeNull("materializer must emit link-bearing intermediate");
        result["schoolReference"]!["link"]!["rel"]!.GetValue<string>().Should().Be("School");

        // _etag is composed as "{ContentVersion}-{variantKey}". MaterializeSingleExternalResponse
        // uses ContentVersion 1; schemaEpoch = first 8 hex of BuildMappingSet()'s
        // EffectiveSchemaHash ("01234567"); format j (JSON); no profile ("_"); links enabled ("l").
        // The link-bearing intermediate does not change this — the etag is representation-variant
        // based, not a hash of the served body.
        result["_etag"]!.GetValue<string>().Should().Be("1-01234567.j._.l");
    }

    [Test]
    public void It_strips_link_subtrees_via_StripReferenceLinks_when_links_disabled()
    {
        var sut = CreateMaterializer(new ResourceLinksOptions { Enabled = false });
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();
        var result = MaterializeSingleExternalResponse(sut, readPlan);

        // Precondition: the materialized intermediate carries link even when the flag is off.
        result["schoolReference"]!["link"].Should().NotBeNull("strip is a separate post-projection pass");

        sut.StripReferenceLinks(result, readPlan);

        result["schoolReference"]!
            .AsObject()
            .Should()
            .NotContainKey("link", "StripReferenceLinks must remove the link subtree when Enabled is false");
    }

    [Test]
    public void It_does_not_strip_link_subtrees_via_StripReferenceLinks_when_links_enabled()
    {
        var sut = CreateMaterializer(new ResourceLinksOptions { Enabled = true });
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();
        var result = MaterializeSingleExternalResponse(sut, readPlan);

        sut.StripReferenceLinks(result, readPlan);

        result["schoolReference"]!
            ["link"]
            .Should()
            .NotBeNull("StripReferenceLinks must be a no-op when ResourceLinksOptions.Enabled is true");
    }

    /// <summary>
    /// Composes the DMS-reconstitution → readable-profile-projection seam end-to-end at the
    /// Backend unit level. Matches the design contract in
    /// <c>link-injection.md:402-406</c>: <c>link</c> lies outside the profile namespace,
    /// so readable-profile projection preserves it whenever the enclosing reference survives.
    /// The Core unit tests in <c>ReadableProfileProjectorTests</c> cover the projector half
    /// in isolation (with hand-built JSON); this test proves the wiring still works when the
    /// link is emitted by DMS reconstitution rather than authored by the test.
    /// </summary>
    [Test]
    public void It_preserves_link_through_readable_profile_projection_with_includeOnly_filter()
    {
        var sut = CreateMaterializer(new ResourceLinksOptions { Enabled = true });
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();
        var materialized = MaterializeSingleExternalResponse(sut, readPlan);

        // Sanity: DMS-reconstituted document carries the link before projection runs.
        materialized["schoolReference"]!
            ["link"]
            .Should()
            .NotBeNull(
                "precondition — DMS must emit the link before we can test that the projector preserves it"
            );

        // IncludeOnly profile that selects the reference object (so it survives) but lists
        // ONLY the identity field `schoolId` under it. The contract is that `link` survives
        // even though the profile never lists it — server-generated subtrees short-circuit
        // rule dispatch (see profiles.md §Profile Namespace).
        var schoolReferenceRule = new ObjectRule(
            Name: "schoolReference",
            MemberSelection: MemberSelection.IncludeOnly,
            LogicalSchema: null,
            Properties: [new PropertyRule("schoolId")],
            NestedObjects: null,
            Collections: null,
            Extensions: null
        );
        var contentType = new ContentTypeDefinition(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [],
            Objects: [schoolReferenceRule],
            Collections: [],
            Extensions: []
        );

        var projector = ReadableProfileProjectorTestExtensions.CreateProductionReadableProfileProjector();
        var projected = projector.Project(
            materialized,
            contentType,
            new HashSet<string>(StringComparer.Ordinal) { "schoolId" }
        );

        var projectedReference = projected["schoolReference"] as JsonObject;
        projectedReference
            .Should()
            .NotBeNull("the reference object must survive — it is the carrier for the link");
        projectedReference!
            ["link"]
            .Should()
            .NotBeNull("link must survive readable-profile projection — it is outside the profile namespace");

        var link = projectedReference["link"] as JsonObject;
        link!["rel"]!
            .GetValue<string>()
            .Should()
            .Be("School", "link.rel must survive byte-equal through the projector");
        link["href"]!
            .GetValue<string>()
            .Should()
            .Be(
                $"/ed-fi/schools/{SchoolDocumentUuid:D}",
                "link.href must survive byte-equal through the projector"
            );
    }

    /// <summary>
    /// Pins the <see cref="RelationalReadMaterializationRequest.MappingSet"/> opt-in contract:
    /// a null <c>MappingSet</c> must route to the no-link reconstitution overload even when
    /// <see cref="ResourceLinksOptions.Enabled"/> is <see langword="true"/>. This guards
    /// against a future regression where the routing predicate in
    /// <see cref="RelationalReadMaterializer.MaterializePage"/> is changed to consult
    /// <c>linksOptions.Enabled</c> first (or any condition other than
    /// <c>request.MappingSet is { }</c>) — such a change would invoke the slug resolver on a
    /// stored-document-mode caller (e.g. <c>DefaultRelationalWriteExecutor.cs:184-191</c>) that
    /// deliberately omits <c>MappingSet</c>. ExternalResponse materialization now also requires
    /// <c>MappingSet</c> (and <c>EtagVariant</c>) to compose the <c>_etag</c> (see
    /// <c>RelationalReadMaterializer.ComposeEtag</c>), so this request throws — but the failure
    /// must come from that etag wiring-bug guard, never from the slug resolver. The throwing
    /// resolver below converts a routing regression into a distinguishable failure: if the
    /// no-link overload were bypassed, the exception message would come from
    /// <see cref="ThrowingSlugResolver"/> instead.
    /// </summary>
    [Test]
    public void It_throws_the_etag_wiring_guard_rather_than_invoking_the_slug_resolver_when_mappingset_is_null()
    {
        var sut = new RelationalReadMaterializer(
            new ThrowingSlugResolver(),
            Microsoft.Extensions.Options.Options.Create(new ResourceLinksOptions { Enabled = true }),
            new EtagComposer()
        );
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();
        object?[] row = [1L, (object?)SchoolDocumentId, 255901];
        var metadata = new DocumentMetadataRow(
            DocumentId: 1L,
            DocumentUuid: AcademicWeekDocumentUuid,
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
        );
        var hydratedPage = new HydratedPage(
            TotalCount: null,
            DocumentMetadata: [metadata],
            TableRowsInDependencyOrder: [new HydratedTableRows(readPlan.Model.Root, [row])],
            DescriptorRowsInPlanOrder: []
        )
        {
            DocumentReferenceLookup = new HydratedDocumentReferenceLookup([
                new DocumentReferenceLookupRow(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId),
            ]),
        };

        // No MappingSet (or EtagVariant) on the request — reconstitution must still take the
        // no-link path first (never invoke the slug resolver), then fail loudly in the etag
        // wiring-bug guard because ExternalResponse materialization requires both inputs.
        Action act = () =>
            sut.MaterializePage(
                new RelationalReadPageMaterializationRequest(
                    readPlan,
                    hydratedPage,
                    RelationalGetRequestReadMode.ExternalResponse
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*EtagVariant*MappingSet*to compose the _etag*")
            .Which.Message.Should()
            .NotContain(
                "ThrowingSlugResolver",
                "MappingSet=null must still route to the no-link overload, not slug resolution"
            );
    }

    /// <summary>
    /// Pins the StoredDocument-mode contract: a read with
    /// <see cref="RelationalGetRequestReadMode.StoredDocument"/> must route to the no-link
    /// reconstitution overload even when the caller supplies a <see cref="MappingSet"/> and a
    /// populated <see cref="HydratedDocumentReferenceLookup"/>. StoredDocument is the internal
    /// read-modify-write shape (see <c>RelationalGetRequestContracts.cs:22</c>); a leaking
    /// server-only <c>link</c> subtree would contaminate stored-state profile projection. The
    /// throwing resolver below converts any silent routing slip into a loud test failure.
    /// </summary>
    [Test]
    public void It_does_not_emit_link_on_stored_document_mode_even_when_mapping_set_and_lookup_are_passed()
    {
        var sut = new RelationalReadMaterializer(
            new ThrowingSlugResolver(),
            Microsoft.Extensions.Options.Options.Create(new ResourceLinksOptions { Enabled = true }),
            new EtagComposer()
        );
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();
        object?[] row = [1L, (object?)SchoolDocumentId, 255901];
        var metadata = new DocumentMetadataRow(
            DocumentId: 1L,
            DocumentUuid: AcademicWeekDocumentUuid,
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
        );
        var hydratedPage = new HydratedPage(
            TotalCount: null,
            DocumentMetadata: [metadata],
            TableRowsInDependencyOrder: [new HydratedTableRows(readPlan.Model.Root, [row])],
            DescriptorRowsInPlanOrder: []
        )
        {
            DocumentReferenceLookup = new HydratedDocumentReferenceLookup([
                new DocumentReferenceLookupRow(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId),
            ]),
        };

        // StoredDocument-mode request with both MappingSet AND DocumentReferenceLookup populated.
        // The materializer must route to the no-link overload anyway — the throwing resolver
        // converts any invocation into a loud failure.
        Action act = () =>
            sut.MaterializePage(
                new RelationalReadPageMaterializationRequest(
                    readPlan,
                    hydratedPage,
                    RelationalGetRequestReadMode.StoredDocument
                )
                {
                    MappingSet = BuildMappingSet(),
                }
            );

        act.Should()
            .NotThrow(
                "StoredDocument-mode reads must route to the no-link overload — never invoke the slug resolver"
            );

        var materialized = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                hydratedPage,
                RelationalGetRequestReadMode.StoredDocument
            )
            {
                MappingSet = BuildMappingSet(),
            }
        );
        materialized.Should().ContainSingle();
        materialized[0].Document["schoolReference"]!
            .AsObject()
            .Should()
            .NotContainKey(
                "link",
                "StoredDocument-mode reads must never carry server-generated link decorations"
            );
    }

    /// <summary>
    /// Pins the GET-by-id contract end-to-end through the single-doc
    /// <see cref="IRelationalReadMaterializer.Materialize"/> overload: when the caller passes
    /// <see cref="RelationalReadMaterializationRequest.DocumentReferenceLookup"/>, the overload
    /// must propagate it into the internally-constructed <see cref="HydratedPage"/> so
    /// reconstitution emits <c>link.rel</c> / <c>link.href</c> on fully-defined references.
    /// A regression here would surface as GET-by-id silently dropping <c>link</c> on every
    /// fully-defined reference while GET-many continues to emit it (the
    /// <see cref="IRelationalReadMaterializer.MaterializePage"/> path is unaffected because the
    /// caller passes the original <see cref="HydratedPage"/>).
    /// </summary>
    [Test]
    public void It_emits_link_on_get_by_id_through_single_document_materialize_overload()
    {
        var sut = CreateMaterializer(new ResourceLinksOptions { Enabled = true });
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();

        object?[] row = [1L, (object?)SchoolDocumentId, 255901];
        var metadata = new DocumentMetadataRow(
            DocumentId: 1L,
            DocumentUuid: AcademicWeekDocumentUuid,
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
        );
        var lookup = new HydratedDocumentReferenceLookup([
            new DocumentReferenceLookupRow(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId),
        ]);

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                metadata,
                [new HydratedTableRows(readPlan.Model.Root, [row])],
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                DocumentReferenceLookup = lookup,
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );

        var schoolReference = result["schoolReference"] as JsonObject;
        schoolReference.Should().NotBeNull("reference object must be present in the reconstituted body");
        var link = schoolReference!["link"] as JsonObject;
        link.Should()
            .NotBeNull(
                "single-doc Materialize must propagate DocumentReferenceLookup so reconstitution can emit link"
            );
        link!["rel"]!.GetValue<string>().Should().Be("School");
        link["href"]!.GetValue<string>().Should().Be($"/ed-fi/schools/{SchoolDocumentUuid:D}");
    }

    /// <summary>
    /// Pins the inverse: a single-doc <see cref="IRelationalReadMaterializer.Materialize"/>
    /// caller that does NOT pass <c>DocumentReferenceLookup</c>, such as descriptor
    /// materialization, gets no <c>link</c> emission even with a <see cref="MappingSet"/> in
    /// scope because the lookup map is empty and <c>EmitReferenceLink</c> returns on the miss.
    /// </summary>
    [Test]
    public void It_omits_link_on_single_document_materialize_when_document_reference_lookup_is_null()
    {
        var sut = CreateMaterializer(new ResourceLinksOptions { Enabled = true });
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();

        object?[] row = [1L, (object?)SchoolDocumentId, 255901];
        var metadata = new DocumentMetadataRow(
            DocumentId: 1L,
            DocumentUuid: AcademicWeekDocumentUuid,
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
        );

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                metadata,
                [new HydratedTableRows(readPlan.Model.Root, [row])],
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );

        result["schoolReference"]!
            .AsObject()
            .Should()
            .NotContainKey(
                "link",
                "no DocumentReferenceLookup on the request means an empty lookup map; emission must miss"
            );
    }

    private static RelationalReadMaterializer CreateMaterializer(ResourceLinksOptions linksOptions) =>
        new(
            new StubSlugResolver(_schoolSlug),
            Microsoft.Extensions.Options.Options.Create(linksOptions),
            new EtagComposer()
        );

    /// <summary>
    /// Drives <see cref="IRelationalReadMaterializer.MaterializePage"/> directly with a
    /// <see cref="HydratedPage"/> that carries the auxiliary
    /// <see cref="HydratedDocumentReferenceLookup"/> populated for the page. The
    /// single-doc <c>Materialize</c> overload now propagates the lookup through (see
    /// <c>It_emits_link_on_get_by_id_through_single_document_materialize_overload</c>); this
    /// helper continues to drive the page entry point so the etag / strip / projection
    /// fixtures here exercise the GET-many seam.
    /// </summary>
    private static JsonNode MaterializeSingleExternalResponse(
        IRelationalReadMaterializer sut,
        ResourceReadPlan readPlan
    )
    {
        object?[] row = [1L, (object?)SchoolDocumentId, 255901];
        var rootTableModel = readPlan.Model.Root;
        var metadata = new DocumentMetadataRow(
            DocumentId: 1L,
            DocumentUuid: AcademicWeekDocumentUuid,
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
        );
        var hydratedPage = new HydratedPage(
            TotalCount: null,
            DocumentMetadata: [metadata],
            TableRowsInDependencyOrder: [new HydratedTableRows(rootTableModel, [row])],
            DescriptorRowsInPlanOrder: []
        )
        {
            DocumentReferenceLookup = new HydratedDocumentReferenceLookup([
                new DocumentReferenceLookupRow(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId),
            ]),
        };

        var materialized = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                hydratedPage,
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );

        materialized.Should().ContainSingle();
        return materialized[0].Document;
    }

    [Test]
    public void It_composes_external_response_etag_from_content_version_and_variant_key()
    {
        var sut = CreateMaterializer(new ResourceLinksOptions { Enabled = true });
        var readPlan = BuildReadPlanWithDocumentReferenceBinding();
        object?[] row = [1L, (object?)SchoolDocumentId, 255901];
        var metadata = new DocumentMetadataRow(
            DocumentId: 1L,
            DocumentUuid: AcademicWeekDocumentUuid,
            ContentVersion: 7L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
        );
        var hydratedPage = new HydratedPage(
            TotalCount: null,
            DocumentMetadata: [metadata],
            TableRowsInDependencyOrder: [new HydratedTableRows(readPlan.Model.Root, [row])],
            DescriptorRowsInPlanOrder: []
        )
        {
            DocumentReferenceLookup = new HydratedDocumentReferenceLookup([
                new DocumentReferenceLookupRow(SchoolDocumentId, SchoolDocumentUuid, SchoolResourceKeyId),
            ]),
        };

        var materialized = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                hydratedPage,
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = BuildMappingSet(),
                EtagVariant = new EtagVariantInputs(null, ResponseFormat.Json),
            }
        );

        materialized.Should().ContainSingle();
        // ContentVersion 7; schemaEpoch = first 8 hex of BuildMappingSet()'s EffectiveSchemaHash
        // ("01234567"); format j (JSON); no profile ("_"); links enabled ("l").
        materialized[0].Document["_etag"]!
            .GetValue<string>()
            .Should()
            .Be("7-01234567.j._.l");
    }

    private static ResourceReadPlan BuildReadPlanWithDocumentReferenceBinding()
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
                    TargetResource: _schoolResource,
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
                    TargetResource: _schoolResource,
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

    private sealed class StubSlugResolver(DocumentLinkSlugTriple slug) : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) => slug;
    }

    private sealed class ThrowingSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
            throw new InvalidOperationException(
                "ThrowingSlugResolver was invoked; the no-link reconstitution overload should have been "
                    + $"selected because the request omitted MappingSet. ResourceKeyId was {resourceKeyId}."
            );
    }
}
