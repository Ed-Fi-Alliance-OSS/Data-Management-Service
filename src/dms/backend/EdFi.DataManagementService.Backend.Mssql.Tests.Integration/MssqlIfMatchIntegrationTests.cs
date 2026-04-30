// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
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

file sealed class MssqlIfMatchAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlIfMatchNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
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

file static class MssqlIfMatchTestSupport
{
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static ServiceProvider CreateResourceServiceProvider()
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

    public static ServiceProvider CreateDescriptorServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static RelationalDocumentStoreRepository ResolveRepository(
        ServiceProvider serviceProvider,
        string connectionString,
        string instanceName
    )
    {
        var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: instanceName,
                    ConnectionString: connectionString,
                    RouteContext: []
                )
            );
        return scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
    }

    public static IDescriptorWriteHandler ResolveDescriptorHandler(
        ServiceProvider serviceProvider,
        string connectionString,
        string instanceName
    )
    {
        var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: instanceName,
                    ConnectionString: connectionString,
                    RouteContext: []
                )
            );
        return scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
    }

    public static UpsertRequest CreateSchoolUpsertRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string requestBodyJson,
        Dictionary<string, string>? headers = null,
        string traceId = "mssql-if-match-test-upsert"
    )
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: schoolIdentity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: headers ?? [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlIfMatchNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlIfMatchAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    public static UpdateRequest CreateSchoolUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string requestBodyJson,
        Dictionary<string, string>? headers = null,
        string traceId = "mssql-if-match-test-update"
    )
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: schoolIdentity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: headers ?? [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlIfMatchNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlIfMatchAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    public static DescriptorWriteRequest CreateDescriptorPostRequest(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string bodyJson,
        string? ifMatchEtag = null
    )
    {
        var body = JsonNode.Parse(bodyJson)!;
        var baseResourceInfo = new BaseResourceInfo(
            new ProjectName(resource.ProjectName),
            new ResourceName(resource.ResourceName),
            true
        );
        var identity = new Core.Model.DescriptorDocument(body).ToDocumentIdentity();
        var referentialId = ReferentialIdCalculator.ReferentialIdFrom(baseResourceInfo, identity);

        return new DescriptorWriteRequest(
            mappingSet,
            resource,
            body,
            new DocumentUuid(Guid.NewGuid()),
            referentialId,
            new TraceId("mssql-if-match-descriptor-post"),
            ifMatchEtag: ifMatchEtag
        );
    }

    public static DescriptorWriteRequest CreateDescriptorPutRequest(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        string bodyJson,
        string? ifMatchEtag = null
    ) =>
        new(
            mappingSet,
            resource,
            JsonNode.Parse(bodyJson)!,
            documentUuid,
            referentialId: null,
            new TraceId("mssql-if-match-descriptor-put"),
            ifMatchEtag: ifMatchEtag
        );

    public static string ExtractEtag(UpsertResult result) =>
        result switch
        {
            UpsertResult.InsertSuccess { ETag: not null } s => s.ETag,
            UpsertResult.UpdateSuccess { ETag: not null } s => s.ETag,
            _ => throw new InvalidOperationException(
                $"Cannot extract ETag from upsert result of type {result.GetType().Name}."
            ),
        };

    public static string ExtractEtag(UpdateResult result) =>
        result switch
        {
            UpdateResult.UpdateSuccess { ETag: not null } s => s.ETag,
            _ => throw new InvalidOperationException(
                $"Cannot extract ETag from update result of type {result.GetType().Name}."
            ),
        };

    /// <summary>
    /// Creates a service provider that replaces <see cref="IRelationalWriteExecutor" /> with a
    /// test seam (<see cref="MssqlStaleNoOpIfMatchExecutorSeam" />) that simulates story AC item 4
    /// (stale no-op retry → If-Match recheck → <c>UpdateFailureETagMisMatch</c>) without any
    /// deadlock risk.  Set <see cref="MssqlStaleNoOpTestState.MutateContentOnFirstAttempt" /> on
    /// the returned <paramref name="testState" /> before issuing the stale PUT request.
    /// No production code is modified.
    /// </summary>
    public static ServiceProvider CreateStaleNoOpFaultingServiceProvider(MssqlStaleNoOpTestState testState)
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        // Expose DefaultRelationalWriteExecutor as a concrete scoped service so the seam
        // can delegate to it on attempt 1 without resolving through the IRelationalWriteExecutor
        // interface (which would cause infinite recursion).
        services.AddScoped<DefaultRelationalWriteExecutor>();

        // Register test state as a singleton so it is shared across all scopes.
        services.AddSingleton(testState);

        // Replace IRelationalWriteExecutor with the seam.
        services.Replace(
            ServiceDescriptor.Scoped<IRelationalWriteExecutor, MssqlStaleNoOpIfMatchExecutorSeam>()
        );

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

// ─── Resource If-Match integration tests ────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Resource_If_Match_Enforcement
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private const string CreateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "Version1",
          "addresses": [
            { "city": "Austin" }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                { "_ext": { "sample": { "zone": "Zone-A" } } }
              ]
            }
          }
        }
        """;

    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "Version2",
          "addresses": [
            { "city": "Dallas" }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                { "_ext": { "sample": { "zone": "Zone-B" } } }
              ]
            }
          }
        }
        """;

    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("aaaaaaaa-0000-0000-0000-111111111111")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

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
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = MssqlIfMatchTestSupport.CreateResourceServiceProvider();
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

    // ── Scenario 1: PUT with matching If-Match returns UpdateSuccess ─────────────
    // Exercises MSSQL-specific UPDLOCK/HOLDLOCK/ROWLOCK hints in
    // RelationalWriteFreshnessChecker and RelationalCommittedRepresentationReader.

    [Test]
    public async Task It_returns_update_success_when_put_if_match_equals_current_etag()
    {
        // Create the resource and capture the committed ETag
        var repository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchResourceMatch"
        );
        var createResult = await repository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(_mappingSet, SchoolDocumentUuid, CreateBodyJson)
        );
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var currentEtag = MssqlIfMatchTestSupport.ExtractEtag(createResult);

        // PUT with the current ETag in the If-Match header
        var updateRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchResourceMatch"
        );
        var updateResult = await updateRepository.UpdateDocumentById(
            MssqlIfMatchTestSupport.CreateSchoolUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                UpdateBodyJson,
                headers: new Dictionary<string, string> { ["If-Match"] = currentEtag },
                traceId: "mssql-if-match-resource-match-update"
            )
        );

        updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    // ── Scenario 2: PUT with stale If-Match returns UpdateFailureETagMisMatch ───
    // Exercises the MSSQL-dialect CheckIfMatchEtagAsync short-circuit path.

    [Test]
    public async Task It_returns_etag_mismatch_when_put_if_match_is_stale()
    {
        // Create the resource
        var repository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchResourceMismatch"
        );
        await repository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(_mappingSet, SchoolDocumentUuid, CreateBodyJson)
        );

        // PUT with a stale ETag that does not match the current committed ETag
        var updateRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchResourceMismatch"
        );
        var updateResult = await updateRepository.UpdateDocumentById(
            MssqlIfMatchTestSupport.CreateSchoolUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                UpdateBodyJson,
                headers: new Dictionary<string, string> { ["If-Match"] = "\"stale-client-etag\"" },
                traceId: "mssql-if-match-resource-mismatch-update"
            )
        );

        updateResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    // ── Scenario 3: PUT with old ETag after an intermediate write returns
    //    UpdateFailureETagMisMatch. Any out-of-band write that bumps the committed
    //    ETag causes a subsequent If-Match check with the old value to fail.
    //    Exercises MSSQL-specific UPDLOCK/HOLDLOCK/ROWLOCK locking semantics.

    [Test]
    public async Task It_returns_etag_mismatch_when_representation_changed_since_client_read()
    {
        // Create the resource and record the initial ETag
        var repository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchResourceIntermediateWrite"
        );
        var createResult = await repository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(_mappingSet, SchoolDocumentUuid, CreateBodyJson)
        );
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var etagAfterCreate = MssqlIfMatchTestSupport.ExtractEtag(createResult);

        // Simulate a concurrent / out-of-band write that changes the representation
        var concurrentRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchResourceIntermediateWrite"
        );
        var concurrentUpdateResult = await concurrentRepository.UpdateDocumentById(
            MssqlIfMatchTestSupport.CreateSchoolUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                UpdateBodyJson,
                traceId: "mssql-if-match-intermediate-write"
            )
        );
        concurrentUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

        var staleClientRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchResourceIntermediateWrite"
        );
        var staleUpdateResult = await staleClientRepository.UpdateDocumentById(
            MssqlIfMatchTestSupport.CreateSchoolUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                CreateBodyJson,
                headers: new Dictionary<string, string> { ["If-Match"] = etagAfterCreate },
                traceId: "mssql-if-match-stale-put"
            )
        );

        staleUpdateResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }
}

// ─── POST (upsert-as-update) If-Match integration tests ──────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_IfMatch_POST_Upsert_As_Update
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private const string CreateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "Version1",
          "addresses": [
            { "city": "Austin" }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                { "_ext": { "sample": { "zone": "Zone-A" } } }
              ]
            }
          }
        }
        """;

    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "Version2",
          "addresses": [
            { "city": "Dallas" }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                { "_ext": { "sample": { "zone": "Zone-B" } } }
              ]
            }
          }
        }
        """;

    private static readonly DocumentUuid PostSchoolDocumentUuid = new(
        Guid.Parse("cccccccc-0000-0000-0000-333333333333")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

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
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = MssqlIfMatchTestSupport.CreateResourceServiceProvider();
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

    // ── Scenario P2-02a: POST-as-update with matching If-Match returns UpdateSuccess

    [Test]
    public async Task It_returns_upsert_success_when_post_if_match_equals_current_etag()
    {
        // Insert the resource and capture the committed ETag
        var insertRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchPostMatch"
        );
        var insertResult = await insertRepository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(
                _mappingSet,
                PostSchoolDocumentUuid,
                CreateBodyJson,
                traceId: "mssql-if-match-post-match-insert"
            )
        );
        insertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var currentEtag = MssqlIfMatchTestSupport.ExtractEtag(insertResult);

        // POST again (upsert-as-update) with the current ETag in the If-Match header
        var updateRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchPostMatch"
        );
        var upsertResult = await updateRepository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(
                _mappingSet,
                PostSchoolDocumentUuid,
                UpdateBodyJson,
                headers: new Dictionary<string, string> { ["If-Match"] = currentEtag },
                traceId: "mssql-if-match-post-match-upsert"
            )
        );

        upsertResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    // ── Scenario P2-02b: POST-as-update with stale If-Match returns
    //    UpsertFailureETagMisMatch

    [Test]
    public async Task It_returns_upsert_failure_etag_mismatch_when_post_if_match_is_stale()
    {
        // Insert the resource and capture the original ETag
        var insertRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchPostMismatch"
        );
        var insertResult = await insertRepository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(
                _mappingSet,
                PostSchoolDocumentUuid,
                CreateBodyJson,
                traceId: "mssql-if-match-post-mismatch-insert"
            )
        );
        insertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var etagAfterInsert = MssqlIfMatchTestSupport.ExtractEtag(insertResult);

        // Bump the ETag by updating the document via PUT
        var putRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchPostMismatch"
        );
        var putResult = await putRepository.UpdateDocumentById(
            MssqlIfMatchTestSupport.CreateSchoolUpdateRequest(
                _mappingSet,
                PostSchoolDocumentUuid,
                UpdateBodyJson,
                traceId: "mssql-if-match-post-mismatch-bump"
            )
        );
        putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

        // POST with the original (now-stale) ETag → must return UpsertFailureETagMisMatch
        var stalePostRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchPostMismatch"
        );
        var upsertResult = await stalePostRepository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(
                _mappingSet,
                PostSchoolDocumentUuid,
                CreateBodyJson,
                headers: new Dictionary<string, string> { ["If-Match"] = etagAfterInsert },
                traceId: "mssql-if-match-post-mismatch-stale-post"
            )
        );

        upsertResult.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
    }
}

// ─── Descriptor If-Match integration tests ──────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Descriptor_If_Match_Enforcement
{
    private MssqlReferenceResolverTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlReferenceResolverTestDatabase.CreateProvisionedAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        await _database.SeedAsync();
        _serviceProvider = MssqlIfMatchTestSupport.CreateDescriptorServiceProvider();
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

    // ── Scenario 4: Descriptor PUT with stale If-Match returns
    //    UpdateFailureETagMisMatch ─────────────────────────────────────────────

    [Test]
    public async Task It_returns_etag_mismatch_when_descriptor_put_if_match_does_not_match_current_etag()
    {
        var resource = _database.Fixture.SchoolTypeDescriptorResource;
        const string BodyJson = """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """;

        var handler = MssqlIfMatchTestSupport.ResolveDescriptorHandler(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchDescriptorMismatch"
        );

        // Create the descriptor
        var createRequest = MssqlIfMatchTestSupport.CreateDescriptorPostRequest(
            _database.MappingSet,
            resource,
            BodyJson
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        insertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var documentUuid = ((UpsertResult.InsertSuccess)insertResult).NewDocumentUuid;

        // PUT with a stale ETag that does not match the current committed descriptor ETag
        var putRequest = MssqlIfMatchTestSupport.CreateDescriptorPutRequest(
            _database.MappingSet,
            resource,
            documentUuid,
            BodyJson,
            ifMatchEtag: "\"stale-client-descriptor-etag\""
        );
        var putResult = await handler.HandlePutAsync(putRequest);

        putResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    // ── Scenario: Descriptor POST-as-update with stale If-Match returns
    //    UpsertFailureETagMisMatch — mirrors PostgreSQL descriptor POST-as-update coverage

    [Test]
    public async Task It_returns_etag_mismatch_when_descriptor_post_as_update_if_match_is_stale()
    {
        var resource = _database.Fixture.SchoolTypeDescriptorResource;
        const string BodyJson = """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """;
        const string UpdatedBodyJson = """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "RegularUpdated"
            }
            """;

        var handler = MssqlIfMatchTestSupport.ResolveDescriptorHandler(
            _serviceProvider,
            _database.ConnectionString,
            "MssqlIfMatchDescriptorPostMismatch"
        );

        // Create the descriptor and capture the committed ETag
        var createRequest = MssqlIfMatchTestSupport.CreateDescriptorPostRequest(
            _database.MappingSet,
            resource,
            BodyJson
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        insertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var etagAfterInsert = MssqlIfMatchTestSupport.ExtractEtag(insertResult);
        var documentUuid = ((UpsertResult.InsertSuccess)insertResult).NewDocumentUuid;

        // Bump the descriptor ETag via PUT (changing shortDescription)
        var putRequest = MssqlIfMatchTestSupport.CreateDescriptorPutRequest(
            _database.MappingSet,
            resource,
            documentUuid,
            UpdatedBodyJson
        );
        var putResult = await handler.HandlePutAsync(putRequest);
        putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

        // POST again with the original (now-stale) ETag → must return UpsertFailureETagMisMatch
        var stalePostRequest = MssqlIfMatchTestSupport.CreateDescriptorPostRequest(
            _database.MappingSet,
            resource,
            BodyJson,
            ifMatchEtag: etagAfterInsert
        );
        var stalePostResult = await handler.HandlePostAsync(stalePostRequest);

        stalePostResult.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
    }
}

// ─── Dependency identity propagation If-Match test ───────────────────────────
//
// Proves story DMS-1005 acceptance criterion 4: If-Match is representation-sensitive
// to dependency identity changes, not just direct writes to the same resource.
//
// Setup: referential-identity fixture (Student allowIdentityUpdates=true + ResourceA
//        referencing Student via studentReference.studentUniqueId).
// Propagation: MSSQL INSTEAD OF UPDATE trigger TR_Student_PropagateIdentity on
//              edfi.Student propagates the new StudentUniqueId to
//              edfi.ResourceA.StudentReference_StudentUniqueId. The TR_ResourceA_Stamp
//              AFTER trigger fires and bumps dms.Document.ContentVersion for ResourceA,
//              changing its computed SHA-256 ETag from E1 to E2.

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_IfMatch_DependencyIdentityChange
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

    private const string StudentUniqueIdV1 = "STU-DEPI-V1";
    private const string StudentUniqueIdV2 = "STU-DEPI-V2";
    private const string ResourceAId = "RA-DEPI-01";

    private static readonly DocumentUuid StudentDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-555555555555")
    );

    private static readonly DocumentUuid ResourceADocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-666666666666")
    );

    private static readonly ResourceInfo StudentResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("Student"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("5.0.0"),
        AllowIdentityUpdates: true,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    private static readonly ResourceInfo ResourceAResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ResourceA"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("5.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

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
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = MssqlIfMatchTestSupport.CreateResourceServiceProvider();
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

    private UpsertRequest CreateStudentUpsertRequest(string studentUniqueId) =>
        new(
            ResourceInfo: StudentResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: new DocumentIdentity([
                    new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), studentUniqueId),
                ]),
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                    StudentResourceInfo,
                    new DocumentIdentity([
                        new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), studentUniqueId),
                    ])
                ),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(
                "{\"studentUniqueId\": \"" + studentUniqueId + "\", \"firstName\": \"Test\"}"
            )!,
            Headers: [],
            TraceId: new TraceId("dep-identity-student-upsert"),
            DocumentUuid: StudentDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlIfMatchNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlIfMatchAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    private UpsertRequest CreateResourceAUpsertRequest(string studentUniqueId)
    {
        var studentIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), studentUniqueId),
        ]);
        var studentBaseInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("Student"),
            false
        );

        return new UpsertRequest(
            ResourceInfo: ResourceAResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: new DocumentIdentity([
                    new DocumentIdentityElement(new JsonPath("$.resourceAId"), ResourceAId),
                    new DocumentIdentityElement(
                        new JsonPath("$.studentReference.studentUniqueId"),
                        studentUniqueId
                    ),
                ]),
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                    ResourceAResourceInfo,
                    new DocumentIdentity([
                        new DocumentIdentityElement(new JsonPath("$.resourceAId"), ResourceAId),
                        new DocumentIdentityElement(
                            new JsonPath("$.studentReference.studentUniqueId"),
                            studentUniqueId
                        ),
                    ])
                ),
                DocumentReferences:
                [
                    new DocumentReference(
                        ResourceInfo: studentBaseInfo,
                        DocumentIdentity: studentIdentity,
                        ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                            studentBaseInfo,
                            studentIdentity
                        ),
                        Path: new JsonPath("$.studentReference")
                    ),
                ],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(
                "{\"resourceAId\": \""
                    + ResourceAId
                    + "\", \"studentReference\": {\"studentUniqueId\": \""
                    + studentUniqueId
                    + "\"}}"
            )!,
            Headers: [],
            TraceId: new TraceId("dep-identity-resource-a-upsert"),
            DocumentUuid: ResourceADocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlIfMatchNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlIfMatchAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    private UpdateRequest CreateResourceAUpdateRequest(string studentUniqueId, string ifMatchEtag)
    {
        var studentIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), studentUniqueId),
        ]);
        var studentBaseInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("Student"),
            false
        );

        return new UpdateRequest(
            ResourceInfo: ResourceAResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: new DocumentIdentity([
                    new DocumentIdentityElement(new JsonPath("$.resourceAId"), ResourceAId),
                    new DocumentIdentityElement(
                        new JsonPath("$.studentReference.studentUniqueId"),
                        studentUniqueId
                    ),
                ]),
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                    ResourceAResourceInfo,
                    new DocumentIdentity([
                        new DocumentIdentityElement(new JsonPath("$.resourceAId"), ResourceAId),
                        new DocumentIdentityElement(
                            new JsonPath("$.studentReference.studentUniqueId"),
                            studentUniqueId
                        ),
                    ])
                ),
                DocumentReferences:
                [
                    new DocumentReference(
                        ResourceInfo: studentBaseInfo,
                        DocumentIdentity: studentIdentity,
                        ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                            studentBaseInfo,
                            studentIdentity
                        ),
                        Path: new JsonPath("$.studentReference")
                    ),
                ],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(
                "{\"resourceAId\": \""
                    + ResourceAId
                    + "\", \"studentReference\": {\"studentUniqueId\": \""
                    + studentUniqueId
                    + "\"}}"
            )!,
            Headers: new Dictionary<string, string> { ["If-Match"] = ifMatchEtag },
            TraceId: new TraceId("dep-identity-stale-put"),
            DocumentUuid: ResourceADocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlIfMatchNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlIfMatchAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    [Test]
    public async Task It_returns_etag_mismatch_when_dependency_identity_change_cascades_to_referrer()
    {
        var repository = MssqlIfMatchTestSupport.ResolveRepository(
            _serviceProvider,
            _database.ConnectionString,
            "DepIdentityChange"
        );

        // Step 1: Insert Student (the dependency) with initial identity V1 via DMS API.
        var studentInsert = await repository.UpsertDocument(CreateStudentUpsertRequest(StudentUniqueIdV1));
        studentInsert.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Step 2: Insert ResourceA (the referrer) referencing Student V1 via DMS API.
        //         Capture the committed ETag E1, computed from the representation that
        //         includes studentReference.studentUniqueId = "STU-DEPI-V1".
        var resourceAInsert = await repository.UpsertDocument(
            CreateResourceAUpsertRequest(StudentUniqueIdV1)
        );
        resourceAInsert.Should().BeOfType<UpsertResult.InsertSuccess>();
        var etagBeforeCascade = MssqlIfMatchTestSupport.ExtractEtag(resourceAInsert);

        // Step 3: Change the Student's identity through the supported propagation path.
        //         The MSSQL INSTEAD OF UPDATE trigger TR_Student_PropagateIdentity on
        //         edfi.Student propagates the new StudentUniqueId value to
        //         edfi.ResourceA.StudentReference_StudentUniqueId. The TR_ResourceA_Stamp
        //         AFTER trigger fires and bumps ContentVersion for ResourceA's dms.Document
        //         row — changing the committed ETag from E1 to E2 — without any direct write
        //         to ResourceA by the test.
        var studentDocId = await _database.ExecuteScalarAsync<long>(
            "SELECT [DocumentId] FROM [dms].[Document] WHERE [DocumentUuid] = @uuid",
            new SqlParameter("uuid", StudentDocumentUuid.Value)
        );
        await _database.ExecuteNonQueryAsync(
            "UPDATE [edfi].[Student] SET [StudentUniqueId] = @newId WHERE [DocumentId] = @docId",
            new SqlParameter("newId", StudentUniqueIdV2),
            new SqlParameter("docId", studentDocId)
        );

        // Step 4: PUT ResourceA with the stale ETag (E1, captured before the cascade).
        //         CheckIfMatchEtagAsync reads the committed representation of ResourceA
        //         (now showing studentReference.studentUniqueId = "STU-DEPI-V2") and
        //         computes E2 ≠ E1 → UpdateFailureETagMisMatch. The mismatch is caused
        //         solely by the dependency identity propagation, not by a direct update
        //         to the referrer.
        var staleUpdateResult = await repository.UpdateDocumentById(
            CreateResourceAUpdateRequest(StudentUniqueIdV2, etagBeforeCascade)
        );

        staleUpdateResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }
}

// ─── Test seam: executor interceptor for stale no-op + If-Match recheck ──────────

// Holds the content-mutation callback configured by the test before the stale PUT.
internal sealed class MssqlStaleNoOpTestState
{
    // Set by the test before issuing the stale PUT.  The seam invokes it on the first
    // ExecuteAsync call, simulating a concurrent content change that arrived after the
    // client captured the ETag but before the retry begins.
    public Func<Task>? MutateContentOnFirstAttempt { get; set; }
}

// Replaces IRelationalWriteExecutor to deterministically reproduce story AC item 4
// (stale no-op retry → If-Match recheck → UpdateFailureETagMisMatch) without deadlock.
//
// On the FIRST call (attempt 0):
//   (a) Invokes MutateContentOnFirstAttempt from a separate service-provider scope that
//       opens its own fresh connection.  At this point the main executor session is NOT
//       yet open because the real executor is never called on attempt 0.  No
//       UPDLOCK/HOLDLOCK/ROWLOCK hint is held, so the mutation does not cause any
//       blocking wait or deadlock.
//   (b) Returns StaleNoOpCompare so the repository retries exactly once.
//
// On the SECOND call (attempt 1) the seam delegates to DefaultRelationalWriteExecutor.
//   CheckIfMatchEtagAsync acquires UPDLOCK/HOLDLOCK/ROWLOCK, reads the now-changed
//   committed ETag (E2), and compares it to the client's stale ETag (E1).
//   Since E2 ≠ E1 the executor returns UpdateFailureETagMisMatch — proving story
//   AC item 4 end-to-end on the SQL Server engine.
file sealed class MssqlStaleNoOpIfMatchExecutorSeam(
    DefaultRelationalWriteExecutor inner,
    MssqlStaleNoOpTestState testState
) : IRelationalWriteExecutor
{
    private bool _firstAttemptCompleted;

    public async Task<RelationalWriteExecutorResult> ExecuteAsync(
        RelationalWriteExecutorRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!_firstAttemptCompleted)
        {
            _firstAttemptCompleted = true;
            if (testState.MutateContentOnFirstAttempt is not null)
            {
                await testState.MutateContentOnFirstAttempt();
            }
            return request.OperationKind switch
            {
                RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureWriteConflict(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                ),
                RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureWriteConflict(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
            };
        }
        return await inner.ExecuteAsync(request, cancellationToken);
    }
}

// ─── Stale no-op + If-Match recheck (story AC item 4) ───────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Stale_NoOp_With_IfMatch
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    // V1 body: used for the initial insert and the stale PUT request.
    private const string SchoolBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "Version1",
          "addresses": [
            { "city": "Austin" }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                { "_ext": { "sample": { "zone": "Zone-A" } } }
              ]
            }
          }
        }
        """;

    // V2 body: used by the mutation injected between attempt 0 and attempt 1 to change
    // the committed content and therefore the committed ETag (E1 → E2).
    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "Version2",
          "addresses": [
            { "city": "Dallas" }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                { "_ext": { "sample": { "zone": "Zone-B" } } }
              ]
            }
          }
        }
        """;

    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-444444444444")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _insertServiceProvider = null!;
    private ServiceProvider _staleNoOpServiceProvider = null!;
    private MssqlStaleNoOpTestState _staleNoOpTestState = null!;

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
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _insertServiceProvider = MssqlIfMatchTestSupport.CreateResourceServiceProvider();
        _staleNoOpTestState = new MssqlStaleNoOpTestState();
        _staleNoOpServiceProvider = MssqlIfMatchTestSupport.CreateStaleNoOpFaultingServiceProvider(
            _staleNoOpTestState
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_insertServiceProvider is not null)
        {
            await _insertServiceProvider.DisposeAsync();
            _insertServiceProvider = null!;
        }

        if (_staleNoOpServiceProvider is not null)
        {
            await _staleNoOpServiceProvider.DisposeAsync();
            _staleNoOpServiceProvider = null!;
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
    public async Task It_returns_update_failure_etag_mismatch_after_stale_no_op_retry()
    {
        // Step 1: insert the document with V1 body and capture its committed ETag (E1).
        var insertRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _insertServiceProvider,
            _database.ConnectionString,
            "MssqlStaleNoOpIfMatchInsert"
        );
        var insertResult = await insertRepository.UpsertDocument(
            MssqlIfMatchTestSupport.CreateSchoolUpsertRequest(
                _mappingSet,
                SchoolDocumentUuid,
                SchoolBodyJson,
                traceId: "mssql-stale-no-op-if-match-insert"
            )
        );
        insertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var etagAfterInsert = MssqlIfMatchTestSupport.ExtractEtag(insertResult);

        // Step 2: configure the mutation the seam will invoke on attempt 0.
        // The mutation updates content to V2 via a fresh connection from a separate scope.
        // At the time it runs, the stale-no-op executor seam has NOT yet opened the main
        // executor session, so no UPDLOCK/HOLDLOCK/ROWLOCK hint is held and there is no
        // deadlock risk.  After the mutation the committed ETag changes from E1 to E2.
        _staleNoOpTestState.MutateContentOnFirstAttempt = async () =>
        {
            var mutationRepository = MssqlIfMatchTestSupport.ResolveRepository(
                _insertServiceProvider,
                _database.ConnectionString,
                "MssqlStaleNoOpIfMatchMutate"
            );
            await mutationRepository.UpdateDocumentById(
                MssqlIfMatchTestSupport.CreateSchoolUpdateRequest(
                    _mappingSet,
                    SchoolDocumentUuid,
                    UpdateBodyJson,
                    traceId: "mssql-stale-no-op-if-match-mutate"
                )
            );
        };

        // Step 3: issue the stale PUT (body = V1, If-Match = E1) through the faulting executor.
        // Attempt 0: seam invokes mutation (E1 → E2) then returns StaleNoOpCompare.
        // Attempt 1: real executor runs, CheckIfMatchEtagAsync reads committed E2 ≠ E1
        //            → UpdateFailureETagMisMatch, proving story AC item 4 on SQL Server.
        var staleNoOpRepository = MssqlIfMatchTestSupport.ResolveRepository(
            _staleNoOpServiceProvider,
            _database.ConnectionString,
            "MssqlStaleNoOpIfMatchPut"
        );
        var putResult = await staleNoOpRepository.UpdateDocumentById(
            MssqlIfMatchTestSupport.CreateSchoolUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                SchoolBodyJson,
                headers: new Dictionary<string, string> { ["If-Match"] = etagAfterInsert },
                traceId: "mssql-stale-no-op-if-match-put"
            )
        );

        putResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }
}
