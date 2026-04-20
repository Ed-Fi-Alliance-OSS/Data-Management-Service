// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Resource choice for Slice 2 profile merge integration tests:
//   NamingStressItem (from the Ed-Fi.DataManagementService.Backend.Ddl.Tests.Unit
//   small/naming-stress fixture). It is single-table, non-descriptor, has an integer
//   identity plus multiple nullable scalar columns (Order, ShortName), and has no
//   document references, descriptor references, or key-unification plans. That lets
//   us exercise hidden-inlined preservation on a flat scalar and create-new without
//   extra fixtures. Fixtures 4/5/6/7/8 from the design spec are deferred because
//   NamingStressItem does not carry the shapes they target (nested scope,
//   document references, descriptors, key unification). Spec scenarios 4 and 5 are
//   covered by the sibling PostgresqlProfileRootTableOnlyMergeFixtureTests against
//   the synthetic profile-root-only-merge fixture.
//
// Fixture 9 is split into 9a (POST) and 9b (PUT) conservative-slice-fence
// fixtures against the School resource from the focused
// stable-key-extension-child-collections fixture. The multi-table success
// case is out of scope because the School catalog contains collection
// scopes.

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
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class ProfileMergeNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class ProfileMergeAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ProfileMergeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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
/// Shared helpers for the Slice 2 root-table-only profile merge integration tests.
/// </summary>
internal static class PostgresqlProfileRootTableOnlyMergeSupport
{
    public const string NamingStressFixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/naming-stress";
    public const string SchoolFixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    public static readonly QualifiedResourceName NamingStressItemResource = new("Ed-Fi", "NamingStressItem");

    public static readonly ResourceInfo NamingStressItemResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("NamingStressItem"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];
        services.AddSingleton<IHostApplicationLifetime, ProfileMergeNoOpHostApplicationLifetime>();
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

    public static DocumentInfo CreateNamingStressDocumentInfo(int namingStressItemId)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.namingStressItemId"),
                namingStressItemId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(NamingStressItemResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        ProfileVisibilityKind rootVisibility,
        System.Collections.Immutable.ImmutableArray<string> rootHiddenMemberPaths,
        bool creatable = true,
        string profileName = "root-only-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        RequestScopeState[] scopeStates =
        [
            new RequestScopeState(
                Address: new ScopeInstanceAddress("$", []),
                Visibility: rootVisibility,
                Creatable: creatable
            ),
        ];
        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: creatable,
                RequestScopeStates: [.. scopeStates],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ConfigurableStoredStateProjectionInvoker(
                rootVisibility,
                rootHiddenMemberPaths
            )
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int namingStressItemId,
        JsonNode body,
        DocumentUuid documentUuid,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootTableOnlyMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: NamingStressItemResourceInfo,
            DocumentInfo: CreateNamingStressDocumentInfo(namingStressItemId),
            MappingSet: mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 1: Profiled PUT with hidden inlined preservation (root scope $)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture 1 (spec ProfileHiddenInlinedColumnPreservation, simplified to one hidden column):
/// seed a NamingStressItem with two scalars (shortName + order). The writable profile
/// declares the root scope visible but marks "order" as a hidden member path. PUT a body
/// that updates only shortName and omits order. Verify that order is preserved from the
/// stored row while shortName is updated.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Put_With_Hidden_Inlined_Column_Preservation
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111")
    );
    private const int NamingStressItemId = 7011;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

        // Seed with both shortName and order set.
        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };
        var seedResult = await PostgresqlProfileRootTableOnlyMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            DocumentUuid,
            "profile-merge-hidden-preservation-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Profiled PUT: only shortName is visible+present; order is hidden-member-path-preserved.
        var writeBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);

        _rowAfterPut = await ReadNamingStressItemRowAsync();
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
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_writes_the_visible_scalar_value() => _rowAfterPut["ShortName"].Should().Be("Updated");

    [Test]
    public void It_preserves_the_hidden_scalar_value() =>
        Convert.ToInt32(_rowAfterPut["Order"], CultureInfo.InvariantCulture).Should().Be(42);

    [Test]
    public void It_preserves_the_identity_column() =>
        Convert
            .ToInt32(_rowAfterPut["NamingStressItemId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NamingStressItemId);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootTableOnlyMerge",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];
        var profileContext = PostgresqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            ProfileVisibilityKind.VisiblePresent,
            ["order"]
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                NamingStressItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-merge-hidden-preservation-put"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    private async Task<IReadOnlyDictionary<string, object?>> ReadNamingStressItemRowAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT nsi."NamingStressItemId", nsi."ShortName", nsi."Order"
            FROM "edfi"."NamingStressItem" nsi
            INNER JOIN "dms"."Document" d ON d."DocumentId" = nsi."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", DocumentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 3: Profiled POST create-new
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture 3: a profiled POST that creates a new root document (no stored state) through
/// the Slice 2 profile merge path. Verifies insert success and that the writable body's
/// values land in the root row. No preservation logic fires when CurrentState is null.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Post_Create_New_For_Root_Only_Resource
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222")
    );
    private const int NamingStressItemId = 7022;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _postResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPost = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

        var writeBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Created",
            ["order"] = 99,
        };
        _postResult = await ExecuteProfiledPostAsync(writeBody);
        _rowAfterPost = await ReadNamingStressItemRowAsync();
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
    public void It_returns_insert_success() => _postResult.Should().BeOfType<UpsertResult.InsertSuccess>();

    [Test]
    public void It_writes_the_identity_value() =>
        Convert
            .ToInt32(_rowAfterPost["NamingStressItemId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NamingStressItemId);

    [Test]
    public void It_writes_the_visible_scalar_values()
    {
        _rowAfterPost["ShortName"].Should().Be("Created");
        Convert.ToInt32(_rowAfterPost["Order"], CultureInfo.InvariantCulture).Should().Be(99);
    }

    private async Task<UpsertResult> ExecuteProfiledPostAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootTableOnlyMerge",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];
        var profileContext = PostgresqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            ProfileVisibilityKind.VisiblePresent,
            []
        );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                NamingStressItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-merge-create-new-post"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private async Task<IReadOnlyDictionary<string, object?>> ReadNamingStressItemRowAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT nsi."NamingStressItemId", nsi."ShortName", nsi."Order"
            FROM "edfi"."NamingStressItem" nsi
            INNER JOIN "dms"."Document" d ON d."DocumentId" = nsi."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", DocumentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 2: Profiled POST-as-update with hidden inlined preservation
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture 2: same preservation invariant as Fixture 1, but exercised through the
/// POST-as-update flow (POST against an existing referential id that the executor
/// resolves as an existing document).
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Post_As_Update_With_Hidden_Inlined_Preservation
{
    private static readonly DocumentUuid SeedDocumentUuid = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333")
    );
    private static readonly DocumentUuid PostAsUpdateDocumentUuid = new(
        Guid.Parse("33333333-4444-4444-4444-333333333333")
    );
    private const int NamingStressItemId = 7033;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _postResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPost = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "OriginalPost",
            ["order"] = 55,
        };
        var seedResult = await PostgresqlProfileRootTableOnlyMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            SeedDocumentUuid,
            "profile-merge-post-as-update-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "UpdatedViaPost",
        };
        _postResult = await ExecuteProfiledPostAsUpdateAsync(writeBody);
        _rowAfterPost = await ReadNamingStressItemRowAsync();
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
    public void It_returns_update_success_via_post_as_update() =>
        _postResult.Should().BeOfType<UpsertResult.UpdateSuccess>();

    [Test]
    public void It_writes_the_visible_scalar_value() =>
        _rowAfterPost["ShortName"].Should().Be("UpdatedViaPost");

    [Test]
    public void It_preserves_the_hidden_scalar_value() =>
        Convert.ToInt32(_rowAfterPost["Order"], CultureInfo.InvariantCulture).Should().Be(55);

    private async Task<UpsertResult> ExecuteProfiledPostAsUpdateAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootTableOnlyMerge",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];
        var profileContext = PostgresqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            ProfileVisibilityKind.VisiblePresent,
            ["order"]
        );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                NamingStressItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-merge-post-as-update-profiled"),
            DocumentUuid: PostAsUpdateDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private async Task<IReadOnlyDictionary<string, object?>> ReadNamingStressItemRowAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT nsi."NamingStressItemId", nsi."ShortName", nsi."Order"
            FROM "edfi"."NamingStressItem" nsi
            INNER JOIN "dms"."Document" d ON d."DocumentId" = nsi."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", SeedDocumentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }
}

// Fixture 4 (VisibleAbsent inlined scope with hidden override) is deferred for this
// resource choice: NamingStressItem is fully flat under the root scope $, so there
// is no nested sub-object scope (like the spec's $.birthData) whose VisibleAbsent
// classification could coexist with a hidden override on a sibling binding. Landing
// this scenario requires a single-table non-descriptor resource with at least one
// inlined sub-object — no authoritative DS 5.2 resource exercised by the existing
// integration suite satisfies that shape, and adding a synthetic ApiSchema fixture
// is out of scope for Slice 2 Task 7.

// Fixture 5 (spec ProfileHiddenInlinedColumnPreservation) is subsumed by Fixture 1
// for this resource. NamingStressItem has only two non-identity nullable scalars
// (shortName, order); Fixture 1 already exercises the "hide one, update the other"
// case. Extending the fixture to more columns would not add coverage against the
// synthesizer, which classifies bindings per-scope rather than per-column count.

// Fixture 6 (hidden document/descriptor reference preservation) is deferred because
// NamingStressItem has no DocumentReference or DescriptorReference bindings.

// Fixture 7 (key-unification hidden contribution + disagreement) is deferred because
// NamingStressItem has no key-unification plans.

// Fixture 8 (synthetic presence hidden-member preservation) is deferred for the same
// reason as Fixture 7.

// Fixture 9 was split into 9a (POST) and 9b (PUT) conservative-slice-fence
// fixtures after the contract-coverage guard in 9edd66fa and the collection-fence
// rule in DMS-1124-2. The multi-table success case is out of scope because the
// School catalog contains collection scopes; single-table success is covered
// elsewhere.

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 9a: Multi-table plan, POST create-new is conservatively fenced by
//  the catalog rule because the School catalog contains collection scopes and
//  Slice 2 has no completeness marker for them.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture 9a: the School catalog contains collection scopes (addresses[*],
/// periods[*], interventions[*], sponsorReferences[*]). Slice 2 has no
/// completeness marker for collection scopes today, so the conservative fence
/// refuses the merge even when the profile emits complete non-collection
/// scope metadata. The profiled POST returns a deterministic slice-fence
/// UnknownFailure and no row is written to the SchoolExtension or School
/// tables.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Post_Multi_Table_Collection_Fence_Returns_Slice_Fence_Failure
{
    private const string RequestBodyJson = """
        {
          "schoolId": 255902
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
    private static readonly DocumentUuid NewDocumentUuid = new(
        Guid.Parse("99999999-9999-9999-9999-99999999aaaa")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _profiledPostResult = null!;
    private int _schoolRowCountAfter;
    private int _extensionRowCountAfter;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.SchoolFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

        _profiledPostResult = await ExecuteProfiledPostAsync();
        _schoolRowCountAfter = await CountRowsAsync(@"""edfi"".""School""");
        _extensionRowCountAfter = await CountRowsAsync(@"""sample"".""SchoolExtension""");
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
    public void It_returns_a_slice_fence_unknown_failure()
    {
        var failure = _profiledPostResult.Should().BeOfType<UpsertResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain("Profile-aware persist");
    }

    [Test]
    public void It_does_not_write_a_school_row() => _schoolRowCountAfter.Should().Be(0);

    [Test]
    public void It_does_not_write_a_school_extension_row() => _extensionRowCountAfter.Should().Be(0);

    private static DocumentInfo CreateSchoolDocumentInfo()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255902"),
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

    private async Task<UpsertResult> ExecuteProfiledPostAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootTableOnlyMergeMultiTableCollectionFencePost",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;

        // Emit complete required non-collection scope metadata so the contract
        // validator is satisfied. Collection scopes in the catalog still cause
        // the conservative fence to fire because Slice 2 has no completeness
        // marker for them.
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var extensionScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$._ext.sample", []),
            Visibility: ProfileVisibilityKind.Hidden,
            Creatable: false
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody.DeepClone(),
                RootResourceCreatable: true,
                RequestScopeStates: [rootScopeState, extensionScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "multi-table-collection-fence-profile-post",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ConfigurableStoredStateProjectionInvoker(
                ProfileVisibilityKind.VisiblePresent,
                []
            )
        );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("profile-merge-multi-table-collection-fence-post"),
            DocumentUuid: NewDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private async Task<int> CountRowsAsync(string quotedTableName)
    {
        var rows = await _database.QueryRowsAsync($"""SELECT COUNT(1) AS "n" FROM {quotedTableName};""");
        return Convert.ToInt32(rows[0]["n"], CultureInfo.InvariantCulture);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 9b: Multi-table plan, PUT existing with complete non-collection
//  scope metadata is conservatively fenced by the collection-catalog rule.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture 9b: the School resource has a multi-table write plan, and this
/// fixture emits complete non-collection scope metadata for an existing visible
/// extension row. The profiled PUT omits the extension scope from the writable
/// request body, marks it VisibleAbsent on the request side, and marks it
/// VisiblePresent on the stored side. Slice 2 must fail deterministically
/// before DML, leaving the stored SchoolExtension column value unchanged.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Put_Multi_Table_Collection_Fence_Returns_Slice_Fence_Failure
{
    // Seed body creates a real SchoolExtension row (sample.campusCode) so the
    // fixture can verify that the conservative fence leaves the stored
    // extension column value untouched — not just that row count is
    // unchanged.
    private const string SeedBodyJson = """
        {
          "schoolId": 255903,
          "_ext": {
            "sample": {
              "campusCode": "SEEDED-CAMPUS-A"
            }
          }
        }
        """;

    // The profiled PUT body omits the extension scope entirely, matching the
    // VisibleAbsent request-side scope state below. If Slice 2 reached
    // separate-table merge logic anyway, the existing visible extension row
    // would be on the delete path; asserting the stored campusCode remains the
    // seed value proves the pre-DML fence protected that row.
    private const string PutBodyJson = """
        {
          "schoolId": 255903
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
        Guid.Parse("99999999-9999-9999-9999-a0000099bbbb")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _profiledPutResult = null!;
    private int _extensionRowCountBefore;
    private int _extensionRowCountAfter;
    private string? _campusCodeBefore;
    private string? _campusCodeAfter;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.SchoolFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

        var seedResult = await ExecuteNonProfiledUpsertAsync();
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        _extensionRowCountBefore = await CountSchoolExtensionRowsAsync();
        _campusCodeBefore = await ReadCampusCodeAsync();
        // Guard against the seed being silently broken: if the extension row
        // didn't land, the "no DML" assertion below would be a tautology.
        _extensionRowCountBefore.Should().Be(1);
        _campusCodeBefore.Should().Be("SEEDED-CAMPUS-A");

        _profiledPutResult = await ExecuteProfiledPutAsync();
        _extensionRowCountAfter = await CountSchoolExtensionRowsAsync();
        _campusCodeAfter = await ReadCampusCodeAsync();
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
    public void It_returns_a_slice_fence_unknown_failure()
    {
        var failure = _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain("Profile-aware persist");
    }

    [Test]
    public void It_does_not_delete_the_school_extension_row() =>
        _extensionRowCountAfter.Should().Be(_extensionRowCountBefore);

    [Test]
    public void It_does_not_overwrite_the_school_extension_column() =>
        _campusCodeAfter.Should().Be(_campusCodeBefore);

    private static DocumentInfo CreateSchoolDocumentInfo()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255903"),
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
                    InstanceName: "PostgresqlProfileRootTableOnlyMergeMultiTableCollectionFence",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(SeedBodyJson)!,
            Headers: [],
            TraceId: new TraceId("profile-merge-multi-table-collection-fence-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileMergeAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlProfileRootTableOnlyMergeMultiTableCollectionFence",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(PutBodyJson)!;

        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);
        var extensionAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var rootRequest = new RequestScopeState(
            Address: rootAddress,
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var extensionRequest = new RequestScopeState(
            Address: extensionAddress,
            Visibility: ProfileVisibilityKind.VisibleAbsent,
            Creatable: false
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody.DeepClone(),
                RootResourceCreatable: true,
                RequestScopeStates: [rootRequest, extensionRequest],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "multi-table-collection-fence-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new TwoNonCollectionScopesStoredStateProjectionInvoker(
                secondScopeJsonScope: "$._ext.sample",
                rootVisibility: ProfileVisibilityKind.VisiblePresent,
                rootHiddenMemberPaths: [],
                secondScopeVisibility: ProfileVisibilityKind.VisiblePresent,
                secondScopeHiddenMemberPaths: []
            )
        );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("profile-merge-multi-table-collection-fence-put"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    private async Task<int> CountSchoolExtensionRowsAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """SELECT COUNT(1) AS "n" FROM "sample"."SchoolExtension";"""
        );
        return Convert.ToInt32(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    private async Task<string?> ReadCampusCodeAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT sx."CampusCode"
            FROM "sample"."SchoolExtension" sx
            INNER JOIN "dms"."Document" d ON d."DocumentId" = sx."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", ExistingDocumentUuid.Value)
        );
        return rows.Count == 0 ? null : rows[0]["CampusCode"] as string;
    }
}
