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
//   NamingStressItem does not carry the shapes they target (nested scope, document
//   references, descriptors, key unification). Spec scenarios 4 and 5 are covered
//   by the sibling MssqlProfileRootTableOnlyMergeFixtureTests against the synthetic
//   profile-root-only-merge fixture. See the pgsql sibling file for detailed
//   rationale.
//
// Fixture 9 (shape-gate) uses the School resource from the focused
// stable-key-extension-child-collections fixture, which has a multi-table write plan.

using System.Data;
using System.Globalization;
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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlProfileMergeAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileMergeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal static class MssqlProfileRootTableOnlyMergeSupport
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
        MssqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "MssqlProfileRootTableOnlyMerge",
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
            UpdateCascadeHandler: new MssqlProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 1: Profiled PUT with hidden inlined preservation (MSSQL)
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Put_With_Hidden_Inlined_Column_Preservation
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("11111111-1111-1111-1111-211111111111")
    );
    private const int NamingStressItemId = 8011;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

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
            MssqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };
        var seedResult = await MssqlProfileRootTableOnlyMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            DocumentUuid,
            "mssql-profile-merge-hidden-preservation-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

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
                    InstanceName: "MssqlProfileRootTableOnlyMerge",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];
        var profileContext = MssqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            ProfileVisibilityKind.VisiblePresent,
            ["order"]
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: MssqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                NamingStressItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-merge-hidden-preservation-put"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileMergeAllowAllResourceAuthorizationHandler(),
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
            SELECT nsi.[NamingStressItemId], nsi.[ShortName], nsi.[Order]
            FROM [edfi].[NamingStressItem] nsi
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = nsi.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", DocumentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 3: Profiled POST create-new (MSSQL)
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Post_Create_New_For_Root_Only_Resource
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222")
    );
    private const int NamingStressItemId = 8022;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _postResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPost = null!;

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
            MssqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

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
                    InstanceName: "MssqlProfileRootTableOnlyMerge",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];
        var profileContext = MssqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            ProfileVisibilityKind.VisiblePresent,
            []
        );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: MssqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                NamingStressItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-merge-create-new-post"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileMergeAllowAllResourceAuthorizationHandler(),
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
            SELECT nsi.[NamingStressItemId], nsi.[ShortName], nsi.[Order]
            FROM [edfi].[NamingStressItem] nsi
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = nsi.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", DocumentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 2: Profiled POST-as-update with hidden inlined preservation (MSSQL)
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Post_As_Update_With_Hidden_Inlined_Preservation
{
    private static readonly DocumentUuid SeedDocumentUuid = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333")
    );
    private static readonly DocumentUuid PostAsUpdateDocumentUuid = new(
        Guid.Parse("33333333-4444-4444-4444-333333333333")
    );
    private const int NamingStressItemId = 8033;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _postResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPost = null!;

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
            MssqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "OriginalPost",
            ["order"] = 55,
        };
        var seedResult = await MssqlProfileRootTableOnlyMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            SeedDocumentUuid,
            "mssql-profile-merge-post-as-update-seed"
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
                    InstanceName: "MssqlProfileRootTableOnlyMerge",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];
        var profileContext = MssqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            ProfileVisibilityKind.VisiblePresent,
            ["order"]
        );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: MssqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                NamingStressItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-merge-post-as-update-profiled"),
            DocumentUuid: PostAsUpdateDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileMergeAllowAllResourceAuthorizationHandler(),
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
            SELECT nsi.[NamingStressItemId], nsi.[ShortName], nsi.[Order]
            FROM [edfi].[NamingStressItem] nsi
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = nsi.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", SeedDocumentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 9: Multi-table plan → Slice 2 shape-gate failure (MSSQL)
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Put_With_Multi_Table_Plan_Returns_Slice_Two_Shape_Gate_Failure
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

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootTableOnlyMergeSupport.SchoolFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootTableOnlyMergeSupport.CreateServiceProvider();

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
                    InstanceName: "MssqlProfileRootTableOnlyMergeShapeGate",
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
            TraceId: new TraceId("mssql-profile-merge-shape-gate-seed"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileMergeAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "MssqlProfileRootTableOnlyMergeShapeGate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writePlan = _mappingSet.WritePlansByResource[SchoolResource];
        var requestBody = JsonNode.Parse(RequestBodyJson)!;

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
            TraceId: new TraceId("mssql-profile-merge-shape-gate-put"),
            DocumentUuid: ExistingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}
