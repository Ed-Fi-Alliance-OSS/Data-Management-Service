// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class PostgresqlProfileIfMatchHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class PostgresqlProfileIfMatchAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class PostgresqlProfileIfMatchNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class PostgresqlProfileIfMatchEtagTestSupport
{
    private const int MaximumPageSize = 25;

    private static readonly ContentTypeDefinition HiddenOrderReadableProfileContentType = new(
        MemberSelection.ExcludeOnly,
        [new PropertyRule("order")],
        [],
        [],
        []
    );

    private static readonly IReadOnlySet<string> NamingStressIdentityPropertyNames = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "namingStressItemId",
    };

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, PostgresqlProfileIfMatchHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton<IReadableProfileProjector, ReadableProfileProjector>();
        services.AddNoOpDocumentLinkSlugResolver();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static ReadableProfileProjectionContext CreateReadableProfileProjectionContext() =>
        new(HiddenOrderReadableProfileContentType, NamingStressIdentityPropertyNames);

    public static BackendProfileWriteContext CreateWritableProfileContext(
        MappingSet mappingSet,
        JsonNode requestBody
    )
    {
        var writePlan = mappingSet.WritePlansByResource[
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];

        return PostgresqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
            writePlan,
            requestBody.DeepClone(),
            ProfileVisibilityKind.VisiblePresent,
            ["order"]
        );
    }

    public static string GetEtag(JsonObject document) =>
        document["_etag"]?.GetValue<string>()
        ?? throw new InvalidOperationException("Expected response document to contain _etag.");

    public static JsonObject GetSingleDocument(QueryResult queryResult)
    {
        var success =
            queryResult as QueryResult.QuerySuccess
            ?? throw new InvalidOperationException("Expected query to succeed.");
        success.EdfiDocs.Should().HaveCount(1);

        return success.EdfiDocs[0]?.AsObject()
            ?? throw new InvalidOperationException("Expected query result to contain a JSON object.");
    }

    public static JsonObject GetSuccessDocument(GetResult getResult)
    {
        var success =
            getResult as GetResult.GetSuccess
            ?? throw new InvalidOperationException("Expected GET to succeed.");

        return success.EdfiDoc.AsObject();
    }

    public static async Task<GetResult> ExecuteGetByIdAsync(
        ServiceProvider serviceProvider,
        string connectionString,
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null
    )
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider, connectionString);

        var request = new RelationalGetRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            MappingSet: mappingSet,
            ResourceAuthorizationHandler: new PostgresqlProfileIfMatchAllowAllResourceAuthorizationHandler(),
            AuthorizationStrategyEvaluators: [],
            TraceId: new TraceId(traceId),
            ReadableProfileProjectionContext: readableProfileProjectionContext
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .GetDocumentById(request);
    }

    public static async Task<QueryResult> ExecuteQueryAsync(
        ServiceProvider serviceProvider,
        string connectionString,
        MappingSet mappingSet,
        string traceId,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null
    )
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider, connectionString);

        var request = new RelationalQueryRequest(
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext([]),
            MappingSet: mappingSet,
            QueryElements: [],
            AuthorizationSecurableInfo: PostgresqlProfileRootTableOnlyMergeSupport
                .NamingStressItemResourceInfo
                .AuthorizationSecurableInfo,
            AuthorizationStrategyEvaluators: [],
            PaginationParameters: new PaginationParameters(
                Limit: MaximumPageSize,
                Offset: 0,
                TotalCount: false,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId(traceId),
            ReadableProfileProjectionContext: readableProfileProjectionContext
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int namingStressItemId,
        JsonNode requestBody,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        await PostgresqlProfileRootTableOnlyMergeSupport.SeedAsync(
            serviceProvider,
            database,
            mappingSet,
            namingStressItemId,
            requestBody,
            documentUuid,
            traceId
        );

    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        string connectionString,
        MappingSet mappingSet,
        int namingStressItemId,
        DocumentUuid documentUuid,
        JsonNode requestBody,
        string traceId,
        string ifMatch
    )
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider, connectionString);

        var request = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                namingStressItemId
            ),
            MappingSet: mappingSet,
            EdfiDoc: requestBody,
            Headers: new Dictionary<string, string> { ["If-Match"] = ifMatch },
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlProfileIfMatchNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileIfMatchAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: CreateWritableProfileContext(mappingSet, requestBody)
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    public static async Task<UpdateResult> ExecuteUnprofiledUpdateAsync(
        ServiceProvider serviceProvider,
        string connectionString,
        MappingSet mappingSet,
        int namingStressItemId,
        DocumentUuid documentUuid,
        JsonNode requestBody,
        string traceId
    )
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider, connectionString);

        var request = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                namingStressItemId
            ),
            MappingSet: mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlProfileIfMatchNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileIfMatchAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private static void SetSelectedInstance(IServiceProvider serviceProvider, string connectionString)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileIfMatchEtag",
                    ConnectionString: connectionString,
                    RouteContext: []
                )
            );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Profiled_Put_Using_The_Current_Profiled_Get_Etag
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("552ebf8d-3467-42f7-a40e-c961fc3efbf1")
    );

    private const int NamingStressItemId = 7411;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GetResult _unprofiledGetBeforeUpdate = null!;
    private GetResult _profiledGetBeforeUpdate = null!;
    private QueryResult _unprofiledQueryBeforeUpdate = null!;
    private QueryResult _profiledQueryBeforeUpdate = null!;
    private UpdateResult _profiledPutResult = null!;
    private GetResult _unprofiledGetAfterUpdate = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };

        var seedResult = await PostgresqlProfileIfMatchEtagTestSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            DocumentUuid,
            "pg-profile-if-match-seed"
        );

        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var readableProfileProjectionContext =
            PostgresqlProfileIfMatchEtagTestSupport.CreateReadableProfileProjectionContext();

        _unprofiledGetBeforeUpdate = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-profile-if-match-get-before"
        );
        _profiledGetBeforeUpdate = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-profile-if-match-get-before-profiled",
            readableProfileProjectionContext
        );
        _unprofiledQueryBeforeUpdate = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteQueryAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            "pg-profile-if-match-query-before"
        );
        _profiledQueryBeforeUpdate = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteQueryAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            "pg-profile-if-match-query-before-profiled",
            readableProfileProjectionContext
        );

        var profiledGetBeforeUpdateDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _profiledGetBeforeUpdate
        );
        var currentProfiledEtag = PostgresqlProfileIfMatchEtagTestSupport.GetEtag(
            profiledGetBeforeUpdateDocument
        );
        var profiledPutBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
        };

        _profiledPutResult = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            profiledPutBody,
            "pg-profile-if-match-profiled-put",
            currentProfiledEtag
        );

        _unprofiledGetAfterUpdate = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-profile-if-match-get-after"
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public void It_preserves_the_full_resource_etag_for_profiled_get_and_query_reads()
    {
        var unprofiledGetDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _unprofiledGetBeforeUpdate
        );
        var profiledGetDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _profiledGetBeforeUpdate
        );
        var unprofiledQueryDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSingleDocument(
            _unprofiledQueryBeforeUpdate
        );
        var profiledQueryDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSingleDocument(
            _profiledQueryBeforeUpdate
        );

        profiledGetDocument["order"].Should().BeNull();
        profiledQueryDocument["order"].Should().BeNull();

        PostgresqlProfileIfMatchEtagTestSupport
            .GetEtag(profiledGetDocument)
            .Should()
            .Be(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetDocument))
            .And.Be(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledQueryDocument))
            .And.Be(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(profiledQueryDocument));
    }

    [Test]
    public void It_returns_the_committed_full_resource_etag_from_the_profiled_put()
    {
        var updateSuccess = _profiledPutResult.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        var unprofiledGetAfterUpdateDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _unprofiledGetAfterUpdate
        );

        updateSuccess.ETag.Should().NotBeNull();
        updateSuccess
            .ETag.Should()
            .Be(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetAfterUpdateDocument));
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Profiled_Put_With_A_Stale_Profiled_Etag_After_A_Hidden_Field_Change
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("a5a91df6-b49d-4a0c-b26c-4437f86bbfd6")
    );

    private const int NamingStressItemId = 7422;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GetResult _unprofiledGetBeforeHiddenChange = null!;
    private GetResult _profiledGetBeforeHiddenChange = null!;
    private GetResult _unprofiledGetAfterHiddenChange = null!;
    private GetResult _profiledGetAfterHiddenChange = null!;
    private UpdateResult _staleProfiledPutResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootTableOnlyMergeSupport.NamingStressFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };

        var seedResult = await PostgresqlProfileIfMatchEtagTestSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            DocumentUuid,
            "pg-profile-if-match-stale-seed"
        );

        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var readableProfileProjectionContext =
            PostgresqlProfileIfMatchEtagTestSupport.CreateReadableProfileProjectionContext();

        _unprofiledGetBeforeHiddenChange = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-profile-if-match-stale-get-before"
        );
        _profiledGetBeforeHiddenChange = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-profile-if-match-stale-get-before-profiled",
            readableProfileProjectionContext
        );

        var hiddenFieldChangeBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 43,
        };

        var hiddenFieldChangeResult =
            await PostgresqlProfileIfMatchEtagTestSupport.ExecuteUnprofiledUpdateAsync(
                _serviceProvider,
                _database.ConnectionString,
                _mappingSet,
                NamingStressItemId,
                DocumentUuid,
                hiddenFieldChangeBody,
                "pg-profile-if-match-hidden-field-change"
            );

        hiddenFieldChangeResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

        _unprofiledGetAfterHiddenChange = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-profile-if-match-stale-get-after"
        );
        _profiledGetAfterHiddenChange = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-profile-if-match-stale-get-after-profiled",
            readableProfileProjectionContext
        );

        var staleProfiledEtag = PostgresqlProfileIfMatchEtagTestSupport.GetEtag(
            PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(_profiledGetBeforeHiddenChange)
        );
        var staleProfiledPutBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
        };

        _staleProfiledPutResult = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            staleProfiledPutBody,
            "pg-profile-if-match-stale-profiled-put",
            staleProfiledEtag
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public void It_changes_the_profiled_etag_when_a_hidden_persisted_field_changes()
    {
        var unprofiledGetBeforeHiddenChangeDocument =
            PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(_unprofiledGetBeforeHiddenChange);
        var profiledGetBeforeHiddenChangeDocument =
            PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(_profiledGetBeforeHiddenChange);
        var unprofiledGetAfterHiddenChangeDocument =
            PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(_unprofiledGetAfterHiddenChange);
        var profiledGetAfterHiddenChangeDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _profiledGetAfterHiddenChange
        );

        profiledGetBeforeHiddenChangeDocument["order"].Should().BeNull();
        profiledGetAfterHiddenChangeDocument["order"].Should().BeNull();

        PostgresqlProfileIfMatchEtagTestSupport
            .GetEtag(profiledGetBeforeHiddenChangeDocument)
            .Should()
            .Be(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetBeforeHiddenChangeDocument));

        PostgresqlProfileIfMatchEtagTestSupport
            .GetEtag(profiledGetAfterHiddenChangeDocument)
            .Should()
            .Be(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetAfterHiddenChangeDocument))
            .And.NotBe(
                PostgresqlProfileIfMatchEtagTestSupport.GetEtag(profiledGetBeforeHiddenChangeDocument)
            );
    }

    [Test]
    public void It_returns_412_for_a_profiled_put_using_the_stale_profiled_etag() =>
        _staleProfiledPutResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
}
