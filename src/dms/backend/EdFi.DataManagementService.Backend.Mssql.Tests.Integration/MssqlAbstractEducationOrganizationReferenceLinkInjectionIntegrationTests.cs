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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

// DMS-1145 task 29b (MSSQL side) — abstract reference link emission. Course's
// educationOrganizationReference is the abstract EducationOrganization binding; the
// concrete target is School. The deterministic resolver registers ONLY School's
// ResourceKeyId, so a miss (wrong resolution) would throw with a clear message rather
// than producing a misleading link.

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Course_With_Abstract_EducationOrganization_Reference
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;
    private const int SchoolId = 255901;
    private const string CourseCode = "ENG-101";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("aaaaaaaa-2b00-0000-0000-000000000001")
    );
    private static readonly DocumentUuid CourseDocumentUuid = new(
        Guid.Parse("bbbbbbbb-2b00-0000-0000-000000000002")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _schoolResourceInfo = null!;
    private ResourceSchema _schoolResourceSchema = null!;
    private ResourceInfo _courseResourceInfo = null!;
    private ResourceSchema _courseResourceSchema = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
        _serviceProvider = CreateServiceProvider();

        (ProjectSchema schoolProjectSchema, ResourceSchema schoolSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "School"
        );
        _schoolResourceInfo = CreateResourceInfo(schoolProjectSchema, schoolSchema);
        _schoolResourceSchema = schoolSchema;

        (ProjectSchema courseProjectSchema, ResourceSchema courseSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "Course"
        );
        _courseResourceInfo = CreateResourceInfo(courseProjectSchema, courseSchema);
        _courseResourceSchema = courseSchema;

        await SeedReferenceDataAsync();

        UpsertResult schoolResult = await UpsertSchoolAsync();
        schoolResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        UpsertResult courseResult = await UpsertCourseAsync();
        courseResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
        }
    }

    [Test]
    public async Task It_emits_concrete_subclass_rel_on_an_abstract_education_organization_reference()
    {
        QueryResult result = await QueryCoursesAsync();

        QueryResult.QuerySuccess success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().HaveCount(1);

        JsonNode courseDocument = success.EdfiDocs[0]!;
        courseDocument["id"]!.GetValue<string>().Should().Be(CourseDocumentUuid.Value.ToString("D"));
        courseDocument["courseCode"]!.GetValue<string>().Should().Be(CourseCode);

        JsonNode edOrgReference = ReferenceLocator.RequireSingle(
            courseDocument,
            "$.educationOrganizationReference"
        );
        edOrgReference["educationOrganizationId"]!.GetValue<long>().Should().Be(SchoolId);

        LinkInjectionAssertions.AssertLink(
            edOrgReference,
            expectedRel: "School",
            expectedProjectEndpointName: "ed-fi",
            expectedEndpointName: "schools",
            expectedDocumentUuid: SchoolDocumentUuid.Value
        );
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

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
            Guid.Parse("c2b00001-0000-0000-0000-000000000001"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("c2b00002-0000-0000-0000-000000000002"),
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
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-29b-seed-school"),
            DocumentUuid: SchoolDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpsertResult> UpsertCourseAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateCourseRequestBody();
        UpsertRequest request = new(
            ResourceInfo: _courseResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _courseResourceInfo,
                _courseResourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-29b-seed-course"),
            DocumentUuid: CourseDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<QueryResult> QueryCoursesAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        RelationalQueryRequest request = new(
            ResourceInfo: _courseResourceInfo,
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
            TraceId: new TraceId("mssql-29b-query-course")
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    private static JsonNode CreateSchoolRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{SchoolId}},
              "nameOfInstitution": "Link Injection 29b Test School",
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

    private static JsonNode CreateCourseRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "educationOrganizationReference": { "educationOrganizationId": {{SchoolId}} },
              "courseCode": "{{CourseCode}}",
              "courseTitle": "English 101",
              "numberOfParts": 1
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
                    Name: "MssqlAbstractRefLinkInjection",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT inserted.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
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
            MERGE INTO [dms].[ReferentialIdentity] AS target
            USING (SELECT @referentialId AS [ReferentialId]) AS source
              ON target.[ReferentialId] = source.[ReferentialId]
            WHEN NOT MATCHED THEN
              INSERT ([ReferentialId], [DocumentId], [ResourceKeyId])
              VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", referentialId.Value),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
