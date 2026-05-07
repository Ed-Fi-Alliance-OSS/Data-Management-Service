// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
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

file sealed class PostgresqlProfileAmbiguousStorageCollapsedNoOpHostApplicationLifetime
    : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class PostgresqlProfileAmbiguousStorageCollapsedAllowAllAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class PostgresqlProfileAmbiguousStorageCollapsedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file sealed class PostgresqlProfileAmbiguousStorageCollapsedProjectionInvoker(
    ImmutableArray<VisibleStoredCollectionRow> visibleStoredRows
) : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    ) =>
        new(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            VisibleStoredCollectionRows: visibleStoredRows
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Profiled_Put_With_Storage_Collapsed_Sibling_Identities
{
    // Reuses the stable-key-update-semantics fixture which defines School with
    // $.addresses[*] whose identity key is "city". Two siblings whose city identity
    // slot is absent (IsPresent: false) vs. explicitly null (IsPresent: true) collapse
    // to the same storage-shape key and must surface as UpdateResult.UnknownFailure.

    private const long SchoolId = 255902;
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("eeee0002-0000-0000-0000-000000000001")
    );
    private const string AddressScope = "$.addresses[*]";
    private const string ProfileName = "top-level-collection-profile";

    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileTopLevelCollectionMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_returns_unknown_failure_when_request_contains_absent_and_explicit_null_identity_siblings()
    {
        // Seed a document so the executor takes the existing-document path (PUT),
        // which is where ValidateWriteContext runs.
        var seedBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, "Austin");
        var seedResult = await SeedAsync(seedBody);
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>("seed must succeed before the PUT");

        // Construct a write body that is structurally valid for the School resource.
        // The body's content does not drive storage-collapsed identity detection: the
        // validator reads the VisibleRequestCollectionItems metadata, not the JSON payload.
        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId);

        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileTopLevelCollectionMergeSupport.SchoolResource
        ];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

        // Extract the compiled identity path for $.addresses[*] ("city").
        var addressIdentityPath = scopeCatalog
            .Single(d => d.JsonScope == AddressScope)
            .SemanticIdentityRelativePathsInOrder.Single();

        var parentAddress = new ScopeInstanceAddress("$", []);

        // Sibling A: identity slot is ABSENT - property not present in JSON at all.
        var absentCityAddress = new CollectionRowAddress(
            AddressScope,
            parentAddress,
            [new SemanticIdentityPart(addressIdentityPath, null, IsPresent: false)]
        );

        // Sibling B: identity slot is EXPLICIT NULL - property present as JSON null.
        var explicitNullCityAddress = new CollectionRowAddress(
            AddressScope,
            parentAddress,
            [new SemanticIdentityPart(addressIdentityPath, null, IsPresent: true)]
        );

        // Both siblings are visible-and-creatable; their storage-collapsed keys are
        // identical (both map to null), but they are presence-aware-distinct.
        var visibleRequestItems = ImmutableArray.Create(
            new VisibleRequestCollectionItem(absentCityAddress, Creatable: true, "$.addresses[0]"),
            new VisibleRequestCollectionItem(explicitNullCityAddress, Creatable: true, "$.addresses[1]")
        );

        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writeBody.DeepClone(),
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: visibleRequestItems
            ),
            ProfileName: ProfileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new PostgresqlProfileAmbiguousStorageCollapsedProjectionInvoker([])
        );

        var result = await ExecuteProfiledPutAsync(writeBody, profileContext);

        result.Should().BeOfType<UpdateResult.UnknownFailure>();
        var failure = (UpdateResult.UnknownFailure)result;
        failure.FailureMessage.Should().StartWith("Profile write contract mismatch:");
        failure
            .FailureMessage.Should()
            .Contain(
                "presence-aware identities that collapse to the same storage-shape key within a parent/scope bucket"
            );
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<
            IHostApplicationLifetime,
            PostgresqlProfileAmbiguousStorageCollapsedNoOpHostApplicationLifetime
        >();
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

    private async Task<UpsertResult> SeedAsync(JsonNode body)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileAmbiguousStorageCollapsed",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                SchoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        var documentInfo = new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: _mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId("pgsql-storage-collapsed-seed"),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlProfileAmbiguousStorageCollapsedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileAmbiguousStorageCollapsedAllowAllAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private async Task<UpdateResult> ExecuteProfiledPutAsync(
        JsonNode writeBody,
        BackendProfileWriteContext profileContext
    )
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileAmbiguousStorageCollapsed",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                SchoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        var documentInfo = new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("pgsql-storage-collapsed-put"),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlProfileAmbiguousStorageCollapsedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileAmbiguousStorageCollapsedAllowAllAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}
