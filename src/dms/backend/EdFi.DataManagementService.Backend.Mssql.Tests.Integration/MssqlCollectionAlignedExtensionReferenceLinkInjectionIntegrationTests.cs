// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
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
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

// DMS-1145 task 29d (MSSQL side) — collection-aligned extension scope reference link
// emission. $.parents[*]._ext.aligned.sponsorReference is a singleton document reference
// in the aligned _ext scope of each parents[*] element. Uses the synthetic fixture
// IntegrationFixtures/profile-collection-aligned-extension-with-doc-ref.

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
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
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
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

        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
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
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
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
            DocumentUuid: SponsorDocumentUuid
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
            TraceId: new TraceId("mssql-29d-seed-parentresource"),
            DocumentUuid: ParentResourceDocumentUuid
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

    /// <summary>
    /// Builds the sponsor DocumentReference entries for every <c>$.parents[*]._ext.aligned.sponsorReference</c>
    /// occurrence in the request body. The synthetic aligned extension exposes the binding shape
    /// but not the full mapping pipeline, so the supplement provides the resolved references directly.
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
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlCollectionAlignedExtLinkInjection",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }
}
