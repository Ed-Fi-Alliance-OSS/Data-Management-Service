// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

// DMS-1145 task 31 — feature-flag tests. Three assertions per the plan:
//   1. Flag on  -> link present, _etag is canonical SHA-256 of the link-bearing body.
//   2. Flag off -> link absent, _etag is canonical SHA-256 of the link-free body.
//   3. Flag flip across restart -> pre-flip _etag does NOT match post-flip body.
//
// "Flip across restart" is simulated by two ordered test methods that share the seeded
// database fixture but each rebuild their own service provider with the opposite Enabled
// value for the link-injection options. The first test captures its etag in a static
// field; the second test asserts divergence.

file sealed class ResourceLinksFlagHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class ResourceLinksFlagAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ResourceLinksFlagNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        System.Text.Json.JsonElement originalEdFiDoc,
        ProjectName originalDocumentProjectName,
        ResourceName originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        ProjectName referencingProjectName,
        ResourceName referencingResourceName
    ) =>
        new(
            OriginalEdFiDoc: referencingEdFiDoc,
            ModifiedEdFiDoc: referencingEdFiDoc,
            Id: referencingDocumentId,
            DocumentPartitionKey: referencingDocumentPartitionKey,
            DocumentUuid: referencingDocumentUuid,
            ProjectName: referencingProjectName,
            ResourceName: referencingResourceName,
            isIdentityUpdate: false
        );
}

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_AcademicWeek_When_The_ResourceLinks_Flag_Is_Flipped_Across_Restart
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;
    private const int SchoolId = 255901;
    private const string WeekIdentifier = "Week-2025-08-15";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("aaaaaaaa-3100-0000-0000-000000000001")
    );
    private static readonly DocumentUuid AcademicWeekDocumentUuid = new(
        Guid.Parse("bbbbbbbb-3100-0000-0000-000000000002")
    );

    // Captured by the flag-on test, asserted against by the flag-off test to prove
    // pre-flip _etag does NOT equal post-flip _etag (the design's "flag flip across
    // restart" divergence requirement).
    private static string? _recordedEtagWithFlagEnabled;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ResourceInfo _schoolResourceInfo = null!;
    private ResourceSchema _schoolResourceSchema = null!;
    private ResourceInfo _academicWeekResourceInfo = null!;
    private ResourceSchema _academicWeekResourceSchema = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _recordedEtagWithFlagEnabled = null;

        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

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

        // Seed phase: writes go through UpsertDocument, which does not exercise
        // ResourceLinksOptions. Reuse a host with the default (Enabled = true) — doesn't
        // affect the persisted state.
        await using ServiceProvider seedServiceProvider = CreateServiceProvider(flagEnabled: true);
        await SeedReferenceDataAsync();
        (await UpsertSchoolAsync(seedServiceProvider)).Should().BeOfType<UpsertResult.InsertSuccess>();
        (await UpsertAcademicWeekAsync(seedServiceProvider)).Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    [Order(1)]
    public async Task It_emits_link_and_canonical_etag_when_flag_is_enabled()
    {
        await using ServiceProvider scopedHost = CreateServiceProvider(flagEnabled: true);

        JsonNode academicWeekDocument = await QuerySingleAcademicWeekAsync(scopedHost);

        // Flag on -> link must be present.
        JsonNode schoolReference = ReferenceLocator.RequireSingle(academicWeekDocument, "$.schoolReference");
        LinkInjectionAssertions.AssertLink(
            schoolReference,
            expectedRel: "School",
            expectedProjectEndpointName: "ed-fi",
            expectedEndpointName: "schools",
            expectedDocumentUuid: SchoolDocumentUuid.Value
        );

        // _etag matches the canonical SHA-256 recompute of the served body (with id,
        // _etag, _lastModifiedDate excluded — see RelationalApiMetadataFormatter.FormatEtag).
        string servedEtag = academicWeekDocument["_etag"]!.GetValue<string>();
        servedEtag.Should().Be(RelationalApiMetadataFormatter.FormatEtag(academicWeekDocument));

        _recordedEtagWithFlagEnabled = servedEtag;
    }

    [Test]
    [Order(2)]
    public async Task It_strips_link_uses_canonical_etag_and_diverges_when_flag_is_disabled()
    {
        _recordedEtagWithFlagEnabled
            .Should()
            .NotBeNull(
                "the flag-on test must run first (it captures the pre-flip etag); check [Order] attributes"
            );

        await using ServiceProvider scopedHost = CreateServiceProvider(flagEnabled: false);

        JsonNode academicWeekDocument = await QuerySingleAcademicWeekAsync(scopedHost);

        // Flag off -> link must be absent.
        JsonNode schoolReference = ReferenceLocator.RequireSingle(academicWeekDocument, "$.schoolReference");
        LinkInjectionAssertions.AssertNoLink(schoolReference);

        // _etag still matches canonical SHA-256 of the served (now link-free) body.
        string servedEtag = academicWeekDocument["_etag"]!.GetValue<string>();
        servedEtag.Should().Be(RelationalApiMetadataFormatter.FormatEtag(academicWeekDocument));

        // The pre-flip-computed _etag does NOT match post-flip responses. This is the
        // explicit divergence the design says clients must expect across a flag flip.
        servedEtag
            .Should()
            .NotBe(
                _recordedEtagWithFlagEnabled,
                "design: pre-flip _etag MUST NOT match post-flip body — clients see the flag flip as an etag change"
            );
    }

    private ServiceProvider CreateServiceProvider(bool flagEnabled)
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, ResourceLinksFlagHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

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
        services.Configure<ResourceLinksOptions>(options => options.Enabled = flagEnabled);

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
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

    private async Task SeedReferenceDataAsync()
    {
        await SeedDescriptorAsync(
            Guid.Parse("c3100001-0000-0000-0000-000000000001"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("c3100002-0000-0000-0000-000000000002"),
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

    private async Task<UpsertResult> UpsertSchoolAsync(ServiceProvider serviceProvider)
    {
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
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
            TraceId: new TraceId("pg-31-seed-school"),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ResourceLinksFlagNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ResourceLinksFlagAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpsertResult> UpsertAcademicWeekAsync(ServiceProvider serviceProvider)
    {
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateAcademicWeekRequestBody();
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
            TraceId: new TraceId("pg-31-seed-academicweek"),
            DocumentUuid: AcademicWeekDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ResourceLinksFlagNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ResourceLinksFlagAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<JsonNode> QuerySingleAcademicWeekAsync(ServiceProvider serviceProvider)
    {
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        RelationalQueryRequest request = new(
            ResourceInfo: _academicWeekResourceInfo,
            MappingSet: _mappingSet,
            QueryElements: [],
            AuthorizationSecurableInfo: _academicWeekResourceInfo.AuthorizationSecurableInfo,
            AuthorizationStrategyEvaluators: [],
            PaginationParameters: new PaginationParameters(
                Limit: 25,
                Offset: 0,
                TotalCount: false,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId("pg-31-query-academicweek")
        );

        QueryResult result = await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);

        QueryResult.QuerySuccess success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().HaveCount(1);

        return success.EdfiDocs[0]!;
    }

    private static JsonNode CreateSchoolRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{SchoolId}},
              "nameOfInstitution": "Link Injection 31 Test School",
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

    private static JsonNode CreateAcademicWeekRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "weekIdentifier": "{{WeekIdentifier}}",
              "schoolReference": { "schoolId": {{SchoolId}} },
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
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlResourceLinksFlag",
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
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
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
            new NpgsqlParameter("documentId", documentId),
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
