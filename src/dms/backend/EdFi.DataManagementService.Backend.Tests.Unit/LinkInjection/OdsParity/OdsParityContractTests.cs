// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.LinkInjection.OdsParity;

// DMS-1145 task 33 — ODS-baseline parity contract for document references.
//
// What this test guarantees: for each committed ODS-shape fixture, the link.rel string
// is byte-equal to the expected resource name and the link.href decomposes (after GUID
// rendering normalization) into the expected (projectEndpointName, endpointName,
// documentUuid:D) triple. Descriptor references are out of scope — they keep their
// canonical-URI string surface.
//
// Important context for reviewers: see fixture-metadata.json. The two committed
// fixtures are synthetic placeholders authored from the design's wire-shape contract
// rather than recorded from a live ODS instance. When a real ODS-recorded fixture is
// obtained, swap the content in place — the parity assertion logic does not change.
[TestFixture]
public class OdsParityContractTests
{
    private const string OdsConcreteSchoolHrefGuidNFormat = "abcd1234ef0156782a3b4c5d6e7f8001";
    private static readonly Guid ExpectedSchoolDocumentUuid = Guid.Parse(OdsConcreteSchoolHrefGuidNFormat);

    private static readonly OdsParityCase[] _parityCases =
    [
        new OdsParityCase(
            Name: "AcademicWeek -> School (concrete root reference)",
            FixtureFileName: "academic-week-school-reference.ods.json",
            ReferenceJsonPath: "$.schoolReference",
            ExpectedRel: "School",
            ExpectedProjectEndpointName: "ed-fi",
            ExpectedEndpointName: "schools",
            ExpectedDocumentUuid: ExpectedSchoolDocumentUuid
        ),
        new OdsParityCase(
            Name: "Course.educationOrganizationReference -> School (abstract -> concrete subclass)",
            FixtureFileName: "course-education-organization-reference.ods.json",
            ReferenceJsonPath: "$.educationOrganizationReference",
            // V1 contract: abstract reference's link.rel is the concrete subclass name
            // (School), NOT the abstract type (EducationOrganization). Same wire value
            // ODS would emit; same value DMS emits via ResourceKeyId-based resolution.
            ExpectedRel: "School",
            ExpectedProjectEndpointName: "ed-fi",
            ExpectedEndpointName: "schools",
            ExpectedDocumentUuid: ExpectedSchoolDocumentUuid
        ),
    ];

    [TestCaseSource(nameof(_parityCases))]
    public void It_emits_link_with_parity_to_ods(OdsParityCase parityCase)
    {
        JsonNode fixtureBody = LoadFixture(parityCase.FixtureFileName);
        JsonNode referenceObject = ReferenceLocator.RequireSingle(fixtureBody, parityCase.ReferenceJsonPath);

        JsonNode link =
            referenceObject["link"]
            ?? throw new InvalidOperationException(
                $"ODS fixture '{parityCase.FixtureFileName}' has no 'link' at '{parityCase.ReferenceJsonPath}'."
            );

        // rel parity: byte-equal.
        link["rel"]!
            .GetValue<string>()
            .Should()
            .Be(parityCase.ExpectedRel, "link.rel must be byte-equal between ODS and DMS");

        // href parity: decompose into projectEndpointName + endpointName + GUID, then
        // normalize the GUID rendering before comparing. ODS uses N format; DMS uses
        // D format; both parse to the same Guid value.
        string href = link["href"]!.GetValue<string>();
        ParsedHref parsed = ParseHref(href);

        parsed
            .ProjectEndpointName.Should()
            .Be(
                parityCase.ExpectedProjectEndpointName,
                "href's projectEndpointName segment must match across ODS and DMS"
            );
        parsed
            .EndpointName.Should()
            .Be(parityCase.ExpectedEndpointName, "href's endpointName segment must match");
        parsed
            .DocumentUuid.Should()
            .Be(
                parityCase.ExpectedDocumentUuid,
                "href's GUID, after normalization, must reference the same logical document"
            );
    }

    // Drives DMS reconstitution end-to-end with semantically equivalent input to the ODS
    // fixture, then compares DMS's emitted link.rel/link.href to the fixture's link.
    // Closes the parity gap that the original case-source test had: the original asserts
    // the fixture matches hardcoded expectations, but never exercises any DMS code, so a
    // future divergence in DMS's link shape would slip through. This variant adds the
    // missing half — the assertion now reads "DMS emits the same wire shape as ODS at this
    // reference site." If a real ODS-recorded fixture replaces the synthetic one, this
    // test continues to verify DMS keeps emitting parity output.
    [TestCaseSource(nameof(_parityCases))]
    public void It_emits_link_byte_equal_to_ods_fixture_via_dms_reconstitution(OdsParityCase parityCase)
    {
        JsonNode fixtureBody = LoadFixture(parityCase.FixtureFileName);
        JsonNode fixtureReference = ReferenceLocator.RequireSingle(fixtureBody, parityCase.ReferenceJsonPath);
        JsonNode fixtureLink =
            fixtureReference["link"]
            ?? throw new InvalidOperationException(
                $"ODS fixture '{parityCase.FixtureFileName}' has no 'link' at '{parityCase.ReferenceJsonPath}'."
            );

        JsonNode dmsDocument = ReconstituteDmsDocumentWithLink(parityCase);
        JsonNode dmsReference = ReferenceLocator.RequireSingle(dmsDocument, parityCase.ReferenceJsonPath);
        JsonNode dmsLink =
            dmsReference["link"]
            ?? throw new InvalidOperationException(
                "DMS reconstitution produced no 'link' at "
                    + $"'{parityCase.ReferenceJsonPath}'. Link injection wiring is broken for this case."
            );

        // rel parity: byte-equal (no normalization needed — both wire formats use the same
        // resource-name casing).
        dmsLink["rel"]!
            .GetValue<string>()
            .Should()
            .Be(
                fixtureLink["rel"]!.GetValue<string>(),
                "DMS link.rel must be byte-equal to the ODS fixture's link.rel"
            );

        // href parity: decompose both sides and compare segments. The GUID rendering
        // intentionally differs between systems (ODS: N format, DMS: D format), so we
        // parse each tail GUID via Guid.Parse and compare the canonical value.
        ParsedHref dmsParsed = ParseHref(dmsLink["href"]!.GetValue<string>());
        ParsedHref fixtureParsed = ParseHref(fixtureLink["href"]!.GetValue<string>());

        dmsParsed
            .ProjectEndpointName.Should()
            .Be(
                fixtureParsed.ProjectEndpointName,
                "DMS and ODS must agree on the projectEndpointName segment"
            );
        dmsParsed
            .EndpointName.Should()
            .Be(fixtureParsed.EndpointName, "DMS and ODS must agree on the endpointName segment");
        dmsParsed
            .DocumentUuid.Should()
            .Be(
                fixtureParsed.DocumentUuid,
                "DMS href GUID (D format) and ODS href GUID (N format) must parse to the same Guid"
            );
    }

    /// <summary>
    /// Drives <see cref="DocumentReconstituter.ReconstitutePage(ResourceReadPlan, HydratedPage, MappingSet, IDocumentLinkSlugResolver)"/>
    /// with the minimal plan + hydrated-page shape needed to emit a link at the parity case's
    /// reference path. The reference's identity field is omitted from the projection (the
    /// parity contract is on link.rel/link.href only — identity fields keep their canonical
    /// surface and are tested elsewhere).
    /// </summary>
    private static JsonNode ReconstituteDmsDocumentWithLink(OdsParityCase parityCase)
    {
        var referencePropertyName = ExtractRootReferencePropertyOrThrow(parityCase.ReferenceJsonPath);
        var rootTable = BuildRootTableForReference(referencePropertyName);
        var readPlan = BuildReadPlanForSingleRootReference(rootTable, referencePropertyName);
        var hydratedPage = BuildSingleDocumentHydratedPageWithLookup(
            rootTable,
            parityCase.ExpectedDocumentUuid
        );
        var slug = new DocumentLinkSlugTriple(
            ProjectEndpointName: parityCase.ExpectedProjectEndpointName,
            EndpointName: parityCase.ExpectedEndpointName,
            ResourceName: parityCase.ExpectedRel
        );

        var documents = DocumentReconstituter.ReconstitutePage(
            readPlan,
            hydratedPage,
            BuildMappingSet(),
            new StubSlugResolver(slug)
        );
        documents.Should().ContainSingle();
        return documents[0];
    }

    private const long DocumentRowId = 1L;
    private const long ReferencedDocumentId = 901L;
    private const short ReferencedResourceKeyId = 7;

    /// <summary>
    /// Extracts the property name from a single-segment-root reference path
    /// (e.g. <c>"$.schoolReference"</c> -> <c>"schoolReference"</c>). The two committed
    /// parity cases both place the reference directly under the document root, so a richer
    /// path parser would be unused. Add segments here when adding nested-collection parity
    /// fixtures.
    /// </summary>
    private static string ExtractRootReferencePropertyOrThrow(string referenceJsonPath)
    {
        if (!referenceJsonPath.StartsWith("$.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Parity case reference path '{referenceJsonPath}' is not a root-level path "
                    + "(expected '$.<property>'). Extend the DMS-reconstitution helper to handle deeper paths."
            );
        }

        var name = referenceJsonPath["$.".Length..];
        if (name.Length == 0 || name.Contains('.') || name.Contains('['))
        {
            throw new InvalidOperationException(
                $"Parity case reference path '{referenceJsonPath}' has segments beyond a single root property; "
                    + "extend the DMS-reconstitution helper to handle deeper paths."
            );
        }

        return name;
    }

    private static DbTableModel BuildRootTableForReference(string referencePropertyName)
    {
        var referencePath = new JsonPathExpression(
            $"$.{referencePropertyName}",
            [new JsonPathSegment.Property(referencePropertyName)]
        );

        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "OdsParityRoot"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_OdsParityRoot",
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
                    ColumnName: new DbColumnName("Reference_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: referencePath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
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
    }

    private static ResourceReadPlan BuildReadPlanForSingleRootReference(
        DbTableModel rootTable,
        string referencePropertyName
    )
    {
        var referencePath = new JsonPathExpression(
            $"$.{referencePropertyName}",
            [new JsonPathSegment.Property(referencePropertyName)]
        );
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "OdsParityRoot"),
            PhysicalSchema: rootTable.Table.Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: referencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("Reference_DocumentId"),
                    TargetResource: schoolResource,
                    IdentityBindings: []
                ),
            ],
            DescriptorEdgeSources: []
        );

        var refProjection = new ReferenceIdentityProjectionTablePlan(
            Table: rootTable.Table,
            BindingsInOrder:
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: referencePath,
                    TargetResource: schoolResource,
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder: []
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
                    FkColumn: new DbColumnName("Reference_DocumentId")
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

    private static HydratedPage BuildSingleDocumentHydratedPageWithLookup(
        DbTableModel rootTable,
        Guid referencedDocumentUuid
    )
    {
        object?[] row = [DocumentRowId, (object?)ReferencedDocumentId];
        var metadata = new DocumentMetadataRow(
            DocumentId: DocumentRowId,
            DocumentUuid: Guid.Parse("0fffffff-0000-0000-0000-000000000001"),
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: DateTimeOffset.UnixEpoch,
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );

        return new HydratedPage(
            TotalCount: null,
            DocumentMetadata: [metadata],
            TableRowsInDependencyOrder: [new HydratedTableRows(rootTable, [row])],
            DescriptorRowsInPlanOrder: []
        )
        {
            DocumentReferenceLookup = new HydratedDocumentReferenceLookup([
                new DocumentReferenceLookupRow(
                    ReferencedDocumentId,
                    referencedDocumentUuid,
                    ReferencedResourceKeyId
                ),
            ]),
        };
    }

    private static MappingSet BuildMappingSet()
    {
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "rmv-ods-parity",
            EffectiveSchemaHash: "0000000000000000000000000000000000000000000000000000000000000000",
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

    private static JsonNode LoadFixture(string fileName)
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "LinkInjection", "OdsParity", fileName);
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException(
                $"ODS parity fixture not found at '{fixturePath}'. Ensure the JSON file is in the test project's LinkInjection/OdsParity folder and the csproj copies it to the output directory."
            );
        }

        string fixtureJson = File.ReadAllText(fixturePath);
        return JsonNode.Parse(fixtureJson)
            ?? throw new InvalidOperationException(
                $"ODS parity fixture '{fileName}' is empty or not valid JSON."
            );
    }

    private static ParsedHref ParseHref(string href)
    {
        // Expected shape: /{projectEndpointName}/{endpointName}/{guid}
        // No trailing slash, no query string. Splits on '/' and ignores the leading empty token.
        string[] segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3)
        {
            throw new InvalidOperationException(
                $"href '{href}' does not match the expected /{{proj}}/{{endpoint}}/{{guid}} shape; got {segments.Length.ToString(CultureInfo.InvariantCulture)} segments."
            );
        }

        if (!Guid.TryParse(segments[2], out Guid documentUuid))
        {
            throw new InvalidOperationException(
                $"href '{href}' final segment '{segments[2]}' is not a parseable GUID (neither N nor D format)."
            );
        }

        return new ParsedHref(
            ProjectEndpointName: segments[0],
            EndpointName: segments[1],
            DocumentUuid: documentUuid
        );
    }

    public sealed record OdsParityCase(
        string Name,
        string FixtureFileName,
        string ReferenceJsonPath,
        string ExpectedRel,
        string ExpectedProjectEndpointName,
        string ExpectedEndpointName,
        Guid ExpectedDocumentUuid
    )
    {
        // NUnit displays this in the test runner output, including the fixture name
        // so a failing case is easy to identify.
        public override string ToString() => Name;
    }

    private sealed record ParsedHref(string ProjectEndpointName, string EndpointName, Guid DocumentUuid);
}
