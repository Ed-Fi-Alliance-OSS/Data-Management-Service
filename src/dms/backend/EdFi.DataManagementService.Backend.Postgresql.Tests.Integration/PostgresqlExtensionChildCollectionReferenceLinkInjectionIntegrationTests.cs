// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
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

// DMS-1145 task 29e — reference inside an extension child-collection. Sample extension
// adds School._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference -> sample.Bus.
// Note: Bus's projectEndpointName is "sample" (NOT "ed-fi"), so the link href is
// /sample/buses/<uuid:D> — the resolver and assertion both reflect that.

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_School_With_Extension_Child_Collection_Bus_Reference
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const int MaximumPageSize = 500;
    private const int SchoolId = 255901;
    private const string BusId = "BUS-001";

    private static readonly QualifiedResourceName BusResource = new("Sample", "Bus");
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("aaaaaaaa-2e00-0000-0000-000000000001")
    );
    private static readonly DocumentUuid BusDocumentUuid = new(
        Guid.Parse("eeeeeeee-2e00-0000-0000-000000000002")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _schoolResourceInfo = null!;
    private ResourceSchema _schoolResourceSchema = null!;
    private ResourceInfo _busResourceInfo = null!;
    private ResourceSchema _busResourceSchema = null!;
    private ResourceInfo _sampleExtSchoolResourceInfo = null!;
    private ResourceSchema _sampleExtSchoolResourceSchema = null!;

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

        (ProjectSchema busProjectSchema, ResourceSchema busSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "sample",
            "Bus"
        );
        _busResourceInfo = CreateResourceInfo(busProjectSchema, busSchema);
        _busResourceSchema = busSchema;

        (ProjectSchema sampleExtSchoolProjectSchema, ResourceSchema sampleExtSchoolSchema) =
            GetResourceSchema(_fixture.EffectiveSchemaSet, "sample", "School");
        _sampleExtSchoolResourceInfo = CreateResourceInfo(
            sampleExtSchoolProjectSchema,
            sampleExtSchoolSchema
        );
        _sampleExtSchoolResourceSchema = sampleExtSchoolSchema;

        await SeedReferenceDataAsync();

        // Bus must be POSTed first — the School body references it through
        // _ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference.
        (await UpsertBusAsync())
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        (await UpsertSchoolAsync()).Should().BeOfType<UpsertResult.InsertSuccess>();
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
    public async Task It_emits_link_on_directlyOwnedBusReference_inside_extension_child_collection()
    {
        QueryResult result = await QuerySchoolsAsync();

        QueryResult.QuerySuccess success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().HaveCount(1);

        JsonNode schoolDocument = success.EdfiDocs[0]!;
        schoolDocument["id"]!.GetValue<string>().Should().Be(SchoolDocumentUuid.Value.ToString("D"));
        schoolDocument["schoolId"]!.GetValue<long>().Should().Be(SchoolId);

        // Reference lives inside the Sample extension's child collection on School:
        //   $._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference
        JsonNode busReference = ReferenceLocator.RequireSingle(
            schoolDocument,
            "$._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference"
        );
        busReference["busId"]!.GetValue<string>().Should().Be(BusId);

        // Bus is in the Sample project — endpoint slug is /sample/buses/<uuid>.
        LinkInjectionAssertions.AssertLink(
            busReference,
            expectedRel: "Bus",
            expectedProjectEndpointName: "sample",
            expectedEndpointName: "buses",
            expectedDocumentUuid: BusDocumentUuid.Value
        );
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        short busResourceKeyId = _mappingSet.ResourceKeyIdByResource[BusResource];
        Dictionary<short, DocumentLinkSlugTriple> slugByResourceKeyId = new()
        {
            [busResourceKeyId] = new DocumentLinkSlugTriple(
                ProjectEndpointName: "sample",
                EndpointName: "buses",
                ResourceName: "Bus"
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
            Guid.Parse("c2e00001-0000-0000-0000-000000000001"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("c2e00002-0000-0000-0000-000000000002"),
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

    private async Task<UpsertResult> UpsertBusAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateBusRequestBody();
        UpsertRequest request = new(
            ResourceInfo: _busResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _busResourceInfo,
                _busResourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-29e-seed-bus"),
            DocumentUuid: BusDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpsertResult> UpsertSchoolAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateSchoolRequestBody();
        UpsertRequest request = new(
            ResourceInfo: _schoolResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _schoolResourceInfo,
                _schoolResourceSchema,
                _mappingSet,
                additionalSources:
                [
                    new RelationalDocumentInfoExtractionSource(
                        _sampleExtSchoolResourceInfo,
                        _sampleExtSchoolResourceSchema,
                        UseReferenceExtraction: false,
                        UseRelationalDescriptorExtraction: false
                    ),
                ],
                supplement: new RelationalDocumentInfoSupplement(
                    DocumentReferences: BuildExtensionBusReferences(requestBody),
                    DocumentReferenceArrays: [],
                    DescriptorReferences: []
                )
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-29e-seed-school"),
            DocumentUuid: SchoolDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<QueryResult> QuerySchoolsAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        RelationalQueryRequest request = new(
            ResourceInfo: _schoolResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext([]),
            MappingSet: _mappingSet,
            QueryElements: [],
            AuthorizationStrategyEvaluators: [],
            PaginationParameters: new PaginationParameters(
                Limit: 25,
                Offset: 0,
                TotalCount: false,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId("pg-29e-query-school")
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    /// <summary>
    /// Builds the bus DocumentReference entries for every
    /// <c>$._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference</c> occurrence in the
    /// request body. The extension's ResourceSchema describes the binding shape but the
    /// reference resolver doesn't pick it up without the additionalSources wiring; the
    /// supplement provides the resolved references directly.
    /// </summary>
    private static IReadOnlyList<DocumentReference> BuildExtensionBusReferences(JsonNode requestBody)
    {
        var directlyOwnedBuses = requestBody["_ext"]?["sample"]?["directlyOwnedBuses"]?.AsArray();
        if (directlyOwnedBuses is null)
        {
            return [];
        }

        List<DocumentReference> references = [];
        for (var index = 0; index < directlyOwnedBuses.Count; index++)
        {
            var busId = directlyOwnedBuses[index]?["directlyOwnedBusReference"]?["busId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(busId))
            {
                continue;
            }

            var busResourceInfo = new BaseResourceInfo(
                new ProjectName("Sample"),
                new ResourceName("Bus"),
                IsDescriptor: false
            );
            var busIdentity = new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.busId"), busId),
            ]);
            references.Add(
                new DocumentReference(
                    ResourceInfo: busResourceInfo,
                    DocumentIdentity: busIdentity,
                    ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(busResourceInfo, busIdentity),
                    Path: new JsonPath(
                        $"$._ext.sample.directlyOwnedBuses[{index.ToString(CultureInfo.InvariantCulture)}].directlyOwnedBusReference"
                    )
                )
            );
        }

        return references;
    }

    private static JsonNode CreateBusRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "busId": "{{BusId}}"
            }
            """
        )!;
    }

    private static JsonNode CreateSchoolRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{SchoolId}},
              "nameOfInstitution": "Link Injection 29e Test School",
              "educationOrganizationCategories": [
                {
                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                }
              ],
              "gradeLevels": [
                {
                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                }
              ],
              "_ext": {
                "sample": {
                  "directlyOwnedBuses": [
                    { "directlyOwnedBusReference": { "busId": "{{BusId}}" } }
                  ]
                }
              }
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
                    Name: "PostgresqlExtChildCollectionLinkInjection",
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
