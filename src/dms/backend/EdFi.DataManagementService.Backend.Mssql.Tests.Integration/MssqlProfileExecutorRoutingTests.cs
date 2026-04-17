// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlProfileRoutingAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileRoutingNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

/// <summary>
/// Concrete <see cref="IStoredStateProjectionInvoker"/> that returns a root-only
/// <see cref="ProfileAppliedWriteContext"/>, sufficient for Slice 1 integration tests.
/// </summary>
file sealed class MssqlRootOnlyStoredStateProjectionInvoker : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var rootAddress = new ScopeInstanceAddress("$", []);
        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            VisibleStoredCollectionRows: []
        );
    }
}

file static class MssqlProfileRoutingTestSupport
{
    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static BackendProfileWriteContext CreateCreatableProfileWriteContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: true,
                RequestScopeStates: [rootScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlRootOnlyStoredStateProjectionInvoker()
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
            StoredStateProjectionInvoker: new MssqlRootOnlyStoredStateProjectionInvoker()
        );
    }

    public static BackendProfileWriteContext CreateCreatableProfileWriteContextWithInlinedCollectionDescendant(
        ResourceWritePlan writePlan,
        JsonNode requestBody
    )
    {
        (string JsonScope, ScopeKind Kind)[] additionalScopes =
        [
            ("$.addresses[*].mileInfo", ScopeKind.NonCollection),
        ];

        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan, additionalScopes);

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
        var inlinedScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$.addresses[*].mileInfo", [addressesAncestor]),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: true,
                RequestScopeStates: [rootScopeState, inlinedScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-profile-with-inlined-descendant",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlRootOnlyStoredStateProjectionInvoker()
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
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Post_Create_Where_Root_Is_Not_Creatable
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

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

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRoutingTestSupport.CreateServiceProvider();

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
                    InstanceName: "MssqlProfileRoutingNonCreatablePost",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var profileWriteContext = MssqlProfileRoutingTestSupport.CreateNonCreatableProfileWriteContext(
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
            TraceId: new TraceId("mssql-profile-non-creatable-post"),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }
}

/// <summary>
/// Verifies that a profiled POST that resolves to post-as-update (existing document)
/// reaches the slice fence and returns a family name like "RootTableOnly",
/// instead of the old broad "DMS-1124 pending" message.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Post_As_Update_Reaching_Slice_Fence
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

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _profiledPostAsUpdateResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRoutingTestSupport.CreateServiceProvider();

        var createResult = await ExecuteNonProfiledUpsertAsync();
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

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
            .Contain("RootTableOnly");
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
                    InstanceName: "MssqlProfileRoutingPostAsUpdate",
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
            TraceId: new TraceId("mssql-profile-post-as-update-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRoutingAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "MssqlProfileRoutingPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;
        var profileWriteContext = MssqlProfileRoutingTestSupport.CreateCreatableProfileWriteContext(
            writePlan,
            requestBody.DeepClone()
        );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-post-as-update-profiled"),
            DocumentUuid: PostAsUpdateDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }
}

/// <summary>
/// Verifies that a profiled PUT targeting an existing document reaches the slice fence
/// and returns a family name like "RootTableOnly", instead of the old broad
/// "DMS-1124 pending" message.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Put_Reaching_Slice_Fence
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

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _profiledPutResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRoutingTestSupport.CreateServiceProvider();

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
    public void It_returns_unknown_failure_with_family_name_in_slice_fence()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult.As<UpdateResult.UnknownFailure>().FailureMessage.Should().Contain("RootTableOnly");
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
                    InstanceName: "MssqlProfileRoutingPut",
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
            TraceId: new TraceId("mssql-profile-put-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRoutingAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "MssqlProfileRoutingPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;
        var profileWriteContext = MssqlProfileRoutingTestSupport.CreateCreatableProfileWriteContext(
            writePlan,
            requestBody.DeepClone()
        );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-put-profiled"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

/// <summary>
/// Verifies that a profiled PUT whose compiled scope catalog contains an inlined scope
/// beneath a top-level collection ancestor routes to the <c>TopLevelCollection</c> fence,
/// not the default <c>RootTableOnly</c> fence.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Put_With_Inlined_Collection_Descendant_Scope
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

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _profiledPutResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRoutingTestSupport.CreateServiceProvider();

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
    public void It_returns_unknown_failure_with_TopLevelCollection_family_in_slice_fence()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult
            .As<UpdateResult.UnknownFailure>()
            .FailureMessage.Should()
            .Contain("TopLevelCollection");
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
                    InstanceName: "MssqlProfileRoutingPutInlinedDescendant",
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
            TraceId: new TraceId("mssql-profile-put-inlined-descendant-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRoutingAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "MssqlProfileRoutingPutInlinedDescendant",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;
        var profileWriteContext =
            MssqlProfileRoutingTestSupport.CreateCreatableProfileWriteContextWithInlinedCollectionDescendant(
                writePlan,
                requestBody.DeepClone()
            );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-put-inlined-descendant-profiled"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRoutingNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRoutingAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileWriteContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}
