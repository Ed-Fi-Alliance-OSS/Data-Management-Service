// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class ProfileRoutingNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class ProfileRoutingAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ProfileRoutingNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class ProfileRoutingTestSupport
{
    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, ProfileRoutingNoOpHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static BackendProfileWriteContext CreateCreatableProfileWriteContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody
    )
    {
        // The extension scope below is intentional. Routing fixtures that use this helper
        // expect the slice-fence family name to appear in the failure message, so the profile
        // must include a non-root scope that forces a non-root-table-only classification.
        // Stored-side metadata must also cover $._ext.sample so the contract validator's
        // required-scope-coverage check passes on existing-document flows; the slice fence
        // then classifies the shape rather than the validator rejecting the request first.
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var extensionScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$._ext.sample", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: true,
                RequestScopeStates: [rootScopeState, extensionScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new TwoNonCollectionScopesStoredStateProjectionInvoker(
                secondScopeJsonScope: "$._ext.sample",
                rootVisibility: ProfileVisibilityKind.VisiblePresent,
                rootHiddenMemberPaths: [],
                secondScopeVisibility: ProfileVisibilityKind.VisiblePresent,
                secondScopeHiddenMemberPaths: []
            )
        );
    }

    public static BackendProfileWriteContext CreateNonCreatableProfileWriteContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: false
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: false,
                RequestScopeStates: [rootScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-non-creatable-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new RootOnlyStoredStateProjectionInvoker()
        );
    }

    public static BackendProfileWriteContext CreateCreatableProfileWriteContextWithInlinedCollectionDescendant(
        ResourceWritePlan writePlan,
        JsonNode requestBody
    )
    {
        // The School fixture has "$.addresses[*]" as a top-level collection. We synthesize an
        // inlined common-type scope beneath that collection (the profile middleware's
        // ContentTypeScopeDiscovery would emit the same shape from a writable content type that
        // declares, say, a `mileInfo` object under `addresses`).
        (string JsonScope, ScopeKind Kind)[] additionalScopes =
        [
            ("$.addresses[*].mileInfo", ScopeKind.NonCollection),
        ];

        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan, additionalScopes);

        // Build a matching ancestor chain so the inlined scope address passes the
        // ProfileWriteContractValidator. The "$.addresses[*]" collection's semantic identity is
        // a single "city" part — see the fixture's relational-model manifest for SchoolAddress.
        var addressesAncestor = new AncestorCollectionInstance(
            JsonScope: "$.addresses[*]",
            SemanticIdentityInOrder:
            [
                new SemanticIdentityPart(
                    RelativePath: "city",
                    Value: JsonValue.Create("Austin"),
                    IsPresent: true
                ),
            ]
        );

        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        // The RootExtension non-collection scope is required on both sides by the
        // contract validator independent of this fixture's focus on the inlined
        // collection descendant.
        var extensionScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$._ext.sample", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var inlinedScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$.addresses[*].mileInfo", [addressesAncestor]),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: true,
                RequestScopeStates: [rootScopeState, extensionScopeState, inlinedScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-profile-with-inlined-descendant",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new TwoNonCollectionScopesStoredStateProjectionInvoker(
                secondScopeJsonScope: "$._ext.sample",
                rootVisibility: ProfileVisibilityKind.VisiblePresent,
                rootHiddenMemberPaths: [],
                secondScopeVisibility: ProfileVisibilityKind.VisiblePresent,
                secondScopeHiddenMemberPaths: []
            )
        );
    }
}

/// <summary>
/// Verifies that a profiled POST for a create-new target returns the typed
/// profile data-policy rejection when <c>RootResourceCreatable</c> is false,
/// instead of the old broad "DMS-1124 pending" message.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Post_Create_Where_Root_Is_Not_Creatable
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private const string RequestBodyJson = """
        {
          "schoolId": 255901
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRoutingTestSupport.CreateServiceProvider();

        _result = await ExecuteProfiledPostAsync();
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
    public void It_returns_profile_data_policy_failure_for_creatability_rejection()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>();
        _result
            .As<UpsertResult.UpsertFailureProfileDataPolicy>()
            .ProfileName.Should()
            .Be("test-non-creatable-profile");
    }

    [Test]
    public void It_does_not_return_the_old_dms_1124_pending_message()
    {
        _result.Should().NotBeOfType<UpsertResult.UnknownFailure>();
    }

    private async Task<UpsertResult> ExecuteProfiledPostAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ProfileRoutingNonCreatablePost",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;
        var profileWriteContext = ProfileRoutingTestSupport.CreateNonCreatableProfileWriteContext(
            writePlan,
            requestBody.DeepClone()
        );

        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: schoolIdentity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("profile-non-creatable-post"),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }
}

/// <summary>
/// Verifies that a profiled POST that resolves to post-as-update (existing document)
/// reaches the slice fence and returns a family name (for the School catalog,
/// the conservative catalog fence dominates at NestedAndExtensionCollections),
/// instead of the old broad "DMS-1124 pending" message.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Post_As_Update_Reaching_Slice_Fence
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private const string RequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin",
              "periods": [
                {
                  "periodName": "Morning"
                }
              ]
            }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North",
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                }
              ],
              "interventions": [
                {
                  "interventionCode": "Attendance",
                  "visits": [
                    {
                      "visitCode": "Visit-A"
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222")
    );
    private static readonly DocumentUuid PostAsUpdateDocumentUuid = new(
        Guid.Parse("bbbbbbbb-3333-3333-3333-333333333333")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _profiledPostAsUpdateResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRoutingTestSupport.CreateServiceProvider();

        // Step 1: Create the document without a profile so it exists in the database
        var createResult = await ExecuteNonProfiledUpsertAsync();
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Step 2: POST again with a profile context — this resolves to post-as-update
        _profiledPostAsUpdateResult = await ExecuteProfiledPostAsUpdateAsync();
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
    public void It_returns_unknown_failure_with_family_name_in_slice_fence()
    {
        _profiledPostAsUpdateResult.Should().BeOfType<UpsertResult.UnknownFailure>();
        _profiledPostAsUpdateResult
            .As<UpsertResult.UnknownFailure>()
            .FailureMessage.Should()
            .Contain("NestedAndExtensionCollections");
    }

    [Test]
    public void It_does_not_return_the_old_dms_1124_pending_message()
    {
        _profiledPostAsUpdateResult.Should().BeOfType<UpsertResult.UnknownFailure>();
        _profiledPostAsUpdateResult
            .As<UpsertResult.UnknownFailure>()
            .FailureMessage.Should()
            .NotContain("pending DMS-1124");
    }

    private static DocumentInfo CreateSchoolDocumentInfo()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private async Task<UpsertResult> ExecuteNonProfiledUpsertAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ProfileRoutingPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId("profile-post-as-update-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private async Task<UpsertResult> ExecuteProfiledPostAsUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ProfileRoutingPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;
        var profileWriteContext = ProfileRoutingTestSupport.CreateCreatableProfileWriteContext(
            writePlan,
            requestBody.DeepClone()
        );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("profile-post-as-update-profiled"),
            DocumentUuid: PostAsUpdateDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }
}

/// <summary>
/// Verifies that a profiled PUT targeting an existing document reaches the slice fence
/// and returns a family name (for the School catalog, the conservative catalog fence
/// dominates at NestedAndExtensionCollections), instead of the old broad "DMS-1124
/// pending" message.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Put_Reaching_Slice_Fence
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private const string RequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin",
              "periods": [
                {
                  "periodName": "Morning"
                }
              ]
            }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North",
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                }
              ],
              "interventions": [
                {
                  "interventionCode": "Attendance",
                  "visits": [
                    {
                      "visitCode": "Visit-A"
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("cccccccc-4444-4444-4444-444444444444")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _profiledPutResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRoutingTestSupport.CreateServiceProvider();

        // Step 1: Create the document without a profile so it exists in the database
        var createResult = await ExecuteNonProfiledUpsertAsync();
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Step 2: PUT with a profile context — this targets an existing document
        _profiledPutResult = await ExecuteProfiledPutAsync();
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
    public void It_returns_unknown_failure_with_family_name_in_slice_fence()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult
            .As<UpdateResult.UnknownFailure>()
            .FailureMessage.Should()
            .Contain("NestedAndExtensionCollections");
    }

    [Test]
    public void It_does_not_return_the_old_dms_1124_pending_message()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult
            .As<UpdateResult.UnknownFailure>()
            .FailureMessage.Should()
            .NotContain("pending DMS-1124");
    }

    private static DocumentInfo CreateSchoolDocumentInfo()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private async Task<UpsertResult> ExecuteNonProfiledUpsertAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ProfileRoutingPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId("profile-put-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private async Task<UpdateResult> ExecuteProfiledPutAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ProfileRoutingPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;
        var profileWriteContext = ProfileRoutingTestSupport.CreateCreatableProfileWriteContext(
            writePlan,
            requestBody.DeepClone()
        );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("profile-put-profiled"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

/// <summary>
/// Verifies that a profiled PUT whose compiled scope catalog contains an inlined scope
/// beneath a top-level collection ancestor routes to a collection-family fence
/// rather than falling through to <c>RootTableOnly</c>. The School catalog has
/// nested collections too, so Task 4's conservative catalog fence dominates at
/// <c>NestedAndExtensionCollections</c>. Regression guard for the review finding
/// that the slice-fence classifier was ignoring middleware-augmented inlined scopes.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Put_With_Inlined_Collection_Descendant_Scope
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private const string RequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin"
            }
          ]
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("dddddddd-5555-5555-5555-555555555555")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _profiledPutResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRoutingTestSupport.CreateServiceProvider();

        var createResult = await ExecuteNonProfiledUpsertAsync();
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        _profiledPutResult = await ExecuteProfiledPutAsync();
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
    public void It_returns_unknown_failure_with_collection_family_in_slice_fence()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult
            .As<UpdateResult.UnknownFailure>()
            .FailureMessage.Should()
            .Contain("NestedAndExtensionCollections");
    }

    [Test]
    public void It_does_not_fall_back_to_RootTableOnly()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult
            .As<UpdateResult.UnknownFailure>()
            .FailureMessage.Should()
            .NotContain("RootTableOnly");
    }

    private static DocumentInfo CreateSchoolDocumentInfo()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private async Task<UpsertResult> ExecuteNonProfiledUpsertAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ProfileRoutingPutInlinedDescendant",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId("profile-put-inlined-descendant-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private async Task<UpdateResult> ExecuteProfiledPutAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ProfileRoutingPutInlinedDescendant",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;
        var profileWriteContext =
            ProfileRoutingTestSupport.CreateCreatableProfileWriteContextWithInlinedCollectionDescendant(
                writePlan,
                requestBody.DeepClone()
            );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("profile-put-inlined-descendant-profiled"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}
