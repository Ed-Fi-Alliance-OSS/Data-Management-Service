// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
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
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

// DMS-1145 task 29d — collection-aligned extension scope reference link emission.
// Reference shape: $.parents[*]._ext.aligned.sponsorReference (singleton doc ref inside
// the aligned _ext scope on each parents[*] base-collection element). Uses the synthetic
// IntegrationFixtures/profile-collection-aligned-extension-with-doc-ref fixture — see
// task-29d notes for why authoritative/sample doesn't carry this shape.

file sealed class CollectionAlignedExtHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class CollectionAlignedExtAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class CollectionAlignedExtNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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
public class Given_A_Postgresql_ParentResource_With_Collection_Aligned_Extension_Sponsor_Reference
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

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _sponsorResourceInfo = null!;
    private ResourceSchema _sponsorResourceSchema = null!;
    private ResourceInfo _parentResourceInfo = null!;
    private ResourceSchema _parentResourceSchema = null!;
    private ResourceInfo _alignedExtParentResourceInfo = null!;
    private ResourceSchema _alignedExtParentResourceSchema = null!;

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

        (ProjectSchema alignedExtProjectSchema, ResourceSchema alignedExtParentSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "aligned",
            "ParentResource"
        );
        _alignedExtParentResourceInfo = CreateResourceInfo(alignedExtProjectSchema, alignedExtParentSchema);
        _alignedExtParentResourceSchema = alignedExtParentSchema;

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
        parentDocument["parentResourceId"]!.GetValue<int>().Should().Be(ParentResourceId);

        // Path traversal goes: parents (collection) -> [*] (each element) -> _ext.aligned.sponsorReference
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

        services.AddSingleton<IHostApplicationLifetime, CollectionAlignedExtHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

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
            TraceId: new TraceId("pg-29d-seed-sponsor"),
            DocumentUuid: SponsorDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new CollectionAlignedExtNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new CollectionAlignedExtAllowAllResourceAuthorizationHandler(),
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
                _mappingSet,
                additionalSources:
                [
                    new RelationalDocumentInfoExtractionSource(
                        _alignedExtParentResourceInfo,
                        _alignedExtParentResourceSchema,
                        UseReferenceExtraction: false,
                        UseRelationalDescriptorExtraction: false
                    ),
                ],
                supplement: new RelationalDocumentInfoSupplement(
                    DocumentReferences: BuildAlignedSponsorReferences(requestBody),
                    DocumentReferenceArrays: [],
                    DescriptorReferences: []
                )
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-29d-seed-parentresource"),
            DocumentUuid: ParentResourceDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new CollectionAlignedExtNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new CollectionAlignedExtAllowAllResourceAuthorizationHandler(),
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
            TraceId: new TraceId("pg-29d-query-parentresource")
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

    /// <summary>
    /// Builds the sponsor DocumentReference entries for every <c>$.parents[*]._ext.aligned.sponsorReference</c>
    /// occurrence in the request body. The reference resolver normally extracts these from the
    /// extension's ResourceSchema; the synthetic aligned extension exposes the binding shape but
    /// not the full mapping pipeline, so the supplement provides the resolved references directly.
    /// </summary>
    private static IReadOnlyList<DocumentReference> BuildAlignedSponsorReferences(JsonNode requestBody)
    {
        var parents = requestBody["parents"]?.AsArray();
        if (parents is null)
        {
            return [];
        }

        List<DocumentReference> references = [];
        for (var index = 0; index < parents.Count; index++)
        {
            var sponsorName = parents[index]
                ?["_ext"]?["aligned"]?["sponsorReference"]?["sponsorName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sponsorName))
            {
                continue;
            }

            var sponsorResourceInfo = new BaseResourceInfo(
                new ProjectName("Ed-Fi"),
                new ResourceName("Sponsor"),
                IsDescriptor: false
            );
            var sponsorIdentity = new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.sponsorName"), sponsorName),
            ]);
            references.Add(
                new DocumentReference(
                    ResourceInfo: sponsorResourceInfo,
                    DocumentIdentity: sponsorIdentity,
                    ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                        sponsorResourceInfo,
                        sponsorIdentity
                    ),
                    Path: new JsonPath(
                        $"$.parents[{index.ToString(CultureInfo.InvariantCulture)}]._ext.aligned.sponsorReference"
                    )
                )
            );
        }

        return references;
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
                    InstanceName: "PostgresqlCollectionAlignedExtLinkInjection",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }
}
