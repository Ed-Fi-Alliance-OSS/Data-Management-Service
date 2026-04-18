// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Resource choice for Slice 2 profile merge integration tests:
//   NamingStressItem (from the Ed-Fi.DataManagementService.Backend.Ddl.Tests.Unit
//   small/naming-stress fixture). It is single-table, non-descriptor, has an integer
//   identity plus multiple nullable scalar columns (Order, ShortName), and has no
//   document references, descriptor references, or key-unification plans. That lets
//   us exercise hidden-inlined preservation, visible-absent clearing, and create-new
//   without extra fixtures. Fixtures 6/7/8 from the design spec are deferred because
//   NamingStressItem does not carry the shapes they target (document references,
//   descriptors, key unification).
//
// Fixture 9 (shape-gate) uses the School resource from the focused
// stable-key-extension-child-collections fixture, which has a multi-table write plan.

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
/// Test-configurable <see cref="IStoredStateProjectionInvoker"/> that emits a single
/// non-collection stored scope at <c>$</c> with caller-supplied visibility and hidden
/// member paths. Sufficient for Slice 2 root-only merge fixtures that need the
/// synthesizer to see explicit hidden/absent dispositions from the projection.
/// </summary>
internal sealed class ConfigurableStoredStateProjectionInvoker : IStoredStateProjectionInvoker
{
    private readonly ProfileVisibilityKind _rootVisibility;
    private readonly System.Collections.Immutable.ImmutableArray<string> _rootHiddenMemberPaths;

    public ConfigurableStoredStateProjectionInvoker(
        ProfileVisibilityKind rootVisibility,
        System.Collections.Immutable.ImmutableArray<string> rootHiddenMemberPaths
    )
    {
        _rootVisibility = rootVisibility;
        _rootHiddenMemberPaths = rootHiddenMemberPaths;
    }

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
                    Visibility: _rootVisibility,
                    HiddenMemberPaths: _rootHiddenMemberPaths
                ),
            ],
            VisibleStoredCollectionRows: []
        );
    }
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

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 9: Multi-table plan → Slice 2 shape-gate failure
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture 9: exercises the Slice 2 shape gate against a resource whose compiled
/// write plan has more than one table. The profile context classifies the required
/// slice family as RootTableOnly (only $ is a request scope) so the fence does not
/// fire, and the shape gate must return UnknownFailure whose message references
/// the Slice 2 single-table root-only constraint.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Put_With_Multi_Table_Plan_Returns_Slice_Two_Shape_Gate_Failure
{
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
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("99999999-9999-9999-9999-999999999999")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _profiledPutResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.SchoolFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

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
    public void It_returns_unknown_failure_with_slice_two_shape_gate_message()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult
            .As<UpdateResult.UnknownFailure>()
            .FailureMessage.Should()
            .Contain("single-table root-only");
    }

    [Test]
    public void It_references_dms_1124_in_the_shape_gate_message()
    {
        _profiledPutResult.Should().BeOfType<UpdateResult.UnknownFailure>();
        _profiledPutResult.As<UpdateResult.UnknownFailure>().FailureMessage.Should().Contain("DMS-1124");
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
                    InstanceName: "PostgresqlProfileRootTableOnlyMergeShapeGate",
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
            TraceId: new TraceId("profile-merge-shape-gate-seed"),
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
                    InstanceName: "PostgresqlProfileRootTableOnlyMergeShapeGate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;

        // Build a profile context with only the $ scope (no inlined or collection scopes)
        // so the slice-fence classifier returns RootTableOnly. The shape gate then fires
        // because the compiled write plan has more than one table.
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody.DeepClone(),
                RootResourceCreatable: true,
                RequestScopeStates: [rootScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "shape-gate-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ConfigurableStoredStateProjectionInvoker(
                ProfileVisibilityKind.VisiblePresent,
                []
            )
        );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("profile-merge-shape-gate-put"),
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
}
