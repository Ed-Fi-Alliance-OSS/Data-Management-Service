// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Page-level link-injection coverage: a query returning several documents must emit
/// <c>link.rel</c>/<c>link.href</c> on every document's reference, resolved from the page-scoped
/// document-reference lookup rather than from any single document's own read.
/// </summary>
/// <remarks>
/// Runs against the real generated DS-5.2 DDL with a genuine <see cref="MappingSet"/>, so it
/// exercises the production write and read pipeline end to end. The exact contents of the
/// document-reference lookup for multi-column and child-collection sources are asserted separately
/// by <c>Given_A_Postgresql_Page_With_Multi_Column_And_Child_Document_References</c>; this fixture
/// covers the link-emission half over a genuine page.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Page_Of_AcademicWeeks_With_Link_Injection
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private static readonly PageLinkInjectionSeed[] _seeds =
    [
        new(
            SchoolId: 255911,
            WeekIdentifier: "Page-Week-2025-08-15",
            SchoolDocumentUuid: new DocumentUuid(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000011")),
            AcademicWeekDocumentUuid: new DocumentUuid(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000012"))
        ),
        new(
            SchoolId: 255912,
            WeekIdentifier: "Page-Week-2025-08-22",
            SchoolDocumentUuid: new DocumentUuid(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000013")),
            AcademicWeekDocumentUuid: new DocumentUuid(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000014"))
        ),
    ];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _schoolResourceInfo = null!;
    private ResourceSchema _schoolResourceSchema = null!;
    private ResourceInfo _academicWeekResourceInfo = null!;
    private ResourceSchema _academicWeekResourceSchema = null!;
    private QueryResult _queryResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();

        (ProjectSchema schoolProjectSchema, ResourceSchema schoolSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "School"
        );
        _schoolResourceInfo = CreateResourceInfo(schoolProjectSchema, schoolSchema);
        _schoolResourceSchema = schoolSchema;

        (ProjectSchema academicWeekProjectSchema, ResourceSchema academicWeekSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "AcademicWeek"
        );
        _academicWeekResourceInfo = CreateResourceInfo(academicWeekProjectSchema, academicWeekSchema);
        _academicWeekResourceSchema = academicWeekSchema;

        await SeedReferenceDataAsync();

        foreach (PageLinkInjectionSeed seed in _seeds)
        {
            UpsertResult schoolResult = await UpsertSchoolAsync(seed);
            schoolResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            UpsertResult academicWeekResult = await UpsertAcademicWeekAsync(seed);
            academicWeekResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        _queryResult = await QueryAcademicWeeksAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_every_academic_week_in_the_page()
    {
        QueryResult.QuerySuccess success = _queryResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.EdfiDocs.Should().HaveCount(_seeds.Length);
    }

    [Test]
    public void It_emits_link_rel_and_link_href_on_every_document_in_the_page()
    {
        QueryResult.QuerySuccess success = _queryResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        foreach (PageLinkInjectionSeed seed in _seeds)
        {
            JsonNode document = success.EdfiDocs.Single(candidate =>
                string.Equals(
                    candidate!["weekIdentifier"]!.GetValue<string>(),
                    seed.WeekIdentifier,
                    StringComparison.Ordinal
                )
            )!;

            document["id"]!.GetValue<string>().Should().Be(seed.AcademicWeekDocumentUuid.Value.ToString("D"));

            JsonNode schoolReference = document["schoolReference"]!;
            schoolReference.Should().NotBeNull();
            schoolReference["schoolId"]!.GetValue<long>().Should().Be(seed.SchoolId);

            LinkInjectionAssertions.AssertLink(
                schoolReference,
                expectedRel: "School",
                expectedProjectEndpointName: "ed-fi",
                expectedEndpointName: "schools",
                expectedDocumentUuid: seed.SchoolDocumentUuid.Value
            );
        }
    }

    [Test]
    public void It_resolves_each_document_reference_to_its_own_referenced_document()
    {
        QueryResult.QuerySuccess success = _queryResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        string[] hrefsInWeekOrder =
        [
            .. success
                .EdfiDocs.OrderBy(
                    static document => document!["weekIdentifier"]!.GetValue<string>(),
                    StringComparer.Ordinal
                )
                .Select(static document =>
                    document!["schoolReference"]!["link"]!["href"]!.GetValue<string>()
                ),
        ];

        hrefsInWeekOrder
            .Should()
            .Equal(
                [
                    .. _seeds
                        .OrderBy(static seed => seed.WeekIdentifier, StringComparer.Ordinal)
                        .Select(static seed =>
                            $"/ed-fi/schools/{seed.SchoolDocumentUuid.Value.ToString("D")}"
                        ),
                ],
                "a page-scoped lookup must not collapse distinct references onto one referenced document"
            );
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, PrimarySelectingTestDataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlBackendIntegrationTestServices();

        short schoolResourceKeyId = _mappingSet.ResourceKeyIdByResource[SchoolResource];
        Dictionary<short, DocumentLinkSlugTriple> slugByResourceKeyId = new()
        {
            [schoolResourceKeyId] = new DocumentLinkSlugTriple(
                ProjectEndpointName: "ed-fi",
                EndpointName: "schools",
                ResourceName: "School"
            ),
        };
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentLinkSlugResolver>(
                new DeterministicLinkSlugResolver(slugByResourceKeyId)
            )
        );
        services.Configure<ResourceLinksOptions>(static options => options.Enabled = true);

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        EffectiveProjectSchema effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(
            project =>
                string.Equals(
                    project.ProjectEndpointName,
                    projectEndpointName,
                    StringComparison.OrdinalIgnoreCase
                )
        );

        ProjectSchema projectSchema = new(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        JsonNode resourceSchemaNode =
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
            ?? projectSchema
                .GetAllResourceSchemaNodes()
                .SingleOrDefault(node =>
                    string.Equals(
                        node["resourceName"]?.GetValue<string>(),
                        resourceName,
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException(
                $"Could not find resource '{resourceName}' in project '{projectEndpointName}'."
            );

        return (projectSchema, new ResourceSchema(resourceSchemaNode));
    }

    private static ResourceInfo CreateResourceInfo(
        ProjectSchema projectSchema,
        ResourceSchema resourceSchema
    ) =>
        new(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates
        );

    private async Task SeedReferenceDataAsync()
    {
        await SeedDescriptorAsync(
            Guid.Parse("c1000001-0000-0000-0000-000000000001"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("c1000002-0000-0000-0000-000000000002"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
    }

    private async Task SeedDescriptorAsync(
        Guid documentUuid,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        short resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);
        long documentId = await InsertDescriptorAsync(
            documentUuid,
            resourceKeyId,
            discriminator,
            uri,
            @namespace,
            codeValue,
            shortDescription
        );

        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", resourceName, uri),
            documentId,
            resourceKeyId
        );
    }

    private async Task<UpsertResult> UpsertSchoolAsync(PageLinkInjectionSeed seed)
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateSchoolRequestBody(seed);
        UpsertRequest request = new(
            ResourceInfo: _schoolResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _schoolResourceInfo,
                _schoolResourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-page-link-injection-seed-school"),
            DocumentUuid: seed.SchoolDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpsertResult> UpsertAcademicWeekAsync(PageLinkInjectionSeed seed)
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateAcademicWeekRequestBody(seed);
        UpsertRequest request = new(
            ResourceInfo: _academicWeekResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _academicWeekResourceInfo,
                _academicWeekResourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-page-link-injection-seed-academicweek"),
            DocumentUuid: seed.AcademicWeekDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<QueryResult> QueryAcademicWeeksAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        RelationalQueryRequest request = new(
            ResourceInfo: _academicWeekResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext([]),
            MappingSet: _mappingSet,
            QueryElements: [],
            AuthorizationStrategyEvaluators: [],
            Paging: new CollectionPaging.Traditional(
                new PaginationParameters(
                    Limit: 25,
                    Offset: 0,
                    TotalCount: false,
                    MaximumPageSize: MaximumPageSize
                )
            ),
            TraceId: new TraceId("pg-page-link-injection-query-academicweek"),
            PageOrderingMode: PageOrderingMode.DocumentId
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    private static JsonNode CreateSchoolRequestBody(PageLinkInjectionSeed seed)
    {
        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{seed.SchoolId}},
              "nameOfInstitution": "Page Link Injection Test High School {{seed.SchoolId}}",
              "educationOrganizationCategories": [
                {
                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                }
              ],
              "gradeLevels": [
                {
                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                }
              ]
            }
            """
        )!;
    }

    private static JsonNode CreateAcademicWeekRequestBody(PageLinkInjectionSeed seed)
    {
        return JsonNode.Parse(
            $$"""
            {
              "weekIdentifier": "{{seed.WeekIdentifier}}",
              "schoolReference": { "schoolId": {{seed.SchoolId}} },
              "beginDate": "2025-08-15",
              "endDate": "2025-08-22",
              "totalInstructionalDays": 5
            }
            """
        )!;
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlPageLinkInjection",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        long documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task InsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", referentialId.Value),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(new ProjectName(projectName), new ResourceName(resourceName), true),
            new DocumentIdentity([
                new DocumentIdentityElement(
                    DocumentIdentity.DescriptorIdentityJsonPath,
                    descriptorUri.ToLowerInvariant()
                ),
            ])
        );
    }
}

/// <summary>
/// One School plus the AcademicWeek that references it, seeded so the query under test returns a
/// page of several documents whose references resolve to different referenced documents.
/// </summary>
internal sealed record PageLinkInjectionSeed(
    long SchoolId,
    string WeekIdentifier,
    DocumentUuid SchoolDocumentUuid,
    DocumentUuid AcademicWeekDocumentUuid
);
