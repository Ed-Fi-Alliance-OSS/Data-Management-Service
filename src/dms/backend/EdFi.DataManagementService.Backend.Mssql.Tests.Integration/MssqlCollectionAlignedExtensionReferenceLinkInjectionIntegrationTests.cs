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
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

// DMS-1145 task 29d (MSSQL side) — collection-aligned extension scope reference link
// emission. $.parents[*]._ext.aligned.sponsorReference is a singleton document reference
// in the aligned _ext scope of each parents[*] element. Uses the synthetic fixture
// IntegrationFixtures/profile-collection-aligned-extension-with-doc-ref.

file sealed class MssqlCollectionAlignedExtAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlCollectionAlignedExtNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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
[Category("MssqlIntegration")]
public class Given_A_Mssql_ParentResource_With_Collection_Aligned_Extension_Sponsor_Reference
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension-with-doc-ref";
    private const int MaximumPageSize = 500;
    private const int ParentResourceId = 42;
    private const string SponsorName = "Acme Education Sponsor";
    private const string ParentCode = "P-001";

    private static readonly QualifiedResourceName SponsorResource = new("Ed-Fi", "Sponsor");
    private static readonly DocumentUuid SponsorDocumentUuid = new(
        Guid.Parse("ddddddd0-2d00-0000-0000-000000000001")
    );
    private static readonly DocumentUuid ParentResourceDocumentUuid = new(
        Guid.Parse("ddddddd0-2d00-0000-0000-000000000002")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _sponsorResourceInfo = null!;
    private ResourceSchema _sponsorResourceSchema = null!;
    private ResourceInfo _parentResourceInfo = null!;
    private ResourceSchema _parentResourceSchema = null!;

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
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();

        (ProjectSchema sponsorProjectSchema, ResourceSchema sponsorSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "Sponsor"
        );
        _sponsorResourceInfo = CreateResourceInfo(sponsorProjectSchema, sponsorSchema);
        _sponsorResourceSchema = sponsorSchema;

        (ProjectSchema parentProjectSchema, ResourceSchema parentSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "ParentResource"
        );
        _parentResourceInfo = CreateResourceInfo(parentProjectSchema, parentSchema);
        _parentResourceSchema = parentSchema;

        (await UpsertSponsorAsync()).Should().BeOfType<UpsertResult.InsertSuccess>();
        (await UpsertParentResourceAsync()).Should().BeOfType<UpsertResult.InsertSuccess>();
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
    public async Task It_emits_link_on_sponsorReference_inside_collection_aligned_ext_scope()
    {
        QueryResult result = await QueryParentResourcesAsync();

        QueryResult.QuerySuccess success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().HaveCount(1);

        JsonNode parentDocument = success.EdfiDocs[0]!;
        parentDocument["id"]!.GetValue<string>().Should().Be(ParentResourceDocumentUuid.Value.ToString("D"));
        parentDocument["parentResourceId"]!.GetValue<long>().Should().Be(ParentResourceId);

        JsonNode sponsorReference = ReferenceLocator.RequireSingle(
            parentDocument,
            "$.parents[*]._ext.aligned.sponsorReference"
        );
        sponsorReference["sponsorName"]!.GetValue<string>().Should().Be(SponsorName);

        LinkInjectionAssertions.AssertLink(
            sponsorReference,
            expectedRel: "Sponsor",
            expectedProjectEndpointName: "ed-fi",
            expectedEndpointName: "sponsors",
            expectedDocumentUuid: SponsorDocumentUuid.Value
        );
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        short sponsorResourceKeyId = _mappingSet.ResourceKeyIdByResource[SponsorResource];
        Dictionary<short, DocumentLinkSlugTriple> slugByResourceKeyId = new()
        {
            [sponsorResourceKeyId] = new DocumentLinkSlugTriple(
                ProjectEndpointName: "ed-fi",
                EndpointName: "sponsors",
                ResourceName: "Sponsor"
            ),
        };
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentLinkSlugResolver>(
                new DeterministicLinkSlugResolver(slugByResourceKeyId)
            )
        );
        services.AddOptions<ResourceLinksOptions>();

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

    private async Task<UpsertResult> UpsertSponsorAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateSponsorRequestBody();
        UpsertRequest request = new(
            ResourceInfo: _sponsorResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _sponsorResourceInfo,
                _sponsorResourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-29d-seed-sponsor"),
            DocumentUuid: SponsorDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlCollectionAlignedExtNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlCollectionAlignedExtAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpsertResult> UpsertParentResourceAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CreateParentResourceRequestBody();
        UpsertRequest request = new(
            ResourceInfo: _parentResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _parentResourceInfo,
                _parentResourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-29d-seed-parentresource"),
            DocumentUuid: ParentResourceDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlCollectionAlignedExtNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlCollectionAlignedExtAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<QueryResult> QueryParentResourcesAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        RelationalQueryRequest request = new(
            ResourceInfo: _parentResourceInfo,
            MappingSet: _mappingSet,
            QueryElements: [],
            AuthorizationSecurableInfo: _parentResourceInfo.AuthorizationSecurableInfo,
            AuthorizationStrategyEvaluators: [],
            PaginationParameters: new PaginationParameters(
                Limit: 25,
                Offset: 0,
                TotalCount: false,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId("mssql-29d-query-parentresource")
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    private static JsonNode CreateSponsorRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "sponsorName": "{{SponsorName}}"
            }
            """
        )!;
    }

    private static JsonNode CreateParentResourceRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "parentResourceId": {{ParentResourceId}},
              "parents": [
                {
                  "parentCode": "{{ParentCode}}",
                  "parentName": "Parent One",
                  "_ext": {
                    "aligned": {
                      "sponsorReference": {
                        "sponsorName": "{{SponsorName}}"
                      }
                    }
                  }
                }
              ]
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
                    InstanceName: "MssqlCollectionAlignedExtLinkInjection",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }
}
