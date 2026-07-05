// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file static class MssqlProfileIfMatchEtagTestSupport
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

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton<IReadableProfileProjector, ReadableProfileProjector>();
        services.AddNoOpDocumentLinkSlugResolver();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    // Matches the writable profile name used by CreateWritableProfileContext so a profiled read etag
    // and a subsequent profiled write share the same profileCode. As of the 2026-07-04 ADR amendment,
    // profileCode is no longer state-significant for If-Match (EtagMatchProjection.Of compares only
    // ContentVersion + schemaEpoch), so the shared profile name here is incidental to the match rather
    // than required.
    public const string ReadableProfileName = "root-only-profile";

    public static ReadableProfileProjectionContext CreateReadableProfileProjectionContext() =>
        new(HiddenOrderReadableProfileContentType, NamingStressIdentityPropertyNames)
        {
            ProfileName = ReadableProfileName,
        };

    public static BackendProfileWriteContext CreateWritableProfileContext(
        MappingSet mappingSet,
        JsonNode requestBody
    )
    {
        var writePlan = mappingSet.WritePlansByResource[
            MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResource
        ];

        return MssqlProfileRootTableOnlyMergeSupport.CreateProfileContext(
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
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            MappingSet: mappingSet,
            AuthorizationContext: new RelationalAuthorizationContext([]),
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
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext([]),
            MappingSet: mappingSet,
            QueryElements: [],
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
        MssqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int namingStressItemId,
        JsonNode requestBody,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        await MssqlProfileRootTableOnlyMergeSupport.SeedAsync(
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
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: MssqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                namingStressItemId
            ),
            MappingSet: mappingSet,
            EdfiDoc: requestBody,
            Headers: new Dictionary<string, string> { ["If-Match"] = ifMatch },
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
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
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: MssqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                namingStressItemId
            ),
            MappingSet: mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    public static async Task<UpdateResult> ExecuteUnprofiledPutAsync(
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
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: MssqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                namingStressItemId
            ),
            MappingSet: mappingSet,
            EdfiDoc: requestBody,
            Headers: new Dictionary<string, string> { ["If-Match"] = ifMatch },
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    public static async Task<DeleteResult> ExecuteDeleteAsync(
        ServiceProvider serviceProvider,
        string connectionString,
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        string ifMatch
    )
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider, connectionString);

        var request = new DeleteRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            TraceId: new TraceId(traceId),
            Headers: new Dictionary<string, string> { ["If-Match"] = ifMatch },
            MappingSet: mappingSet
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .DeleteDocumentById(request);
    }

    public static async Task<UpsertResult> ExecutePostAsync(
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

        var request = new UpsertRequest(
            ResourceInfo: MssqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: MssqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
                namingStressItemId
            ),
            MappingSet: mappingSet,
            EdfiDoc: requestBody,
            Headers: new Dictionary<string, string> { ["If-Match"] = ifMatch },
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private static void SetSelectedInstance(IServiceProvider serviceProvider, string connectionString)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlProfileIfMatchEtag",
                    ConnectionString: connectionString,
                    RouteContext: []
                )
            );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Profiled_Put_Using_The_Current_Profiled_Get_Etag
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("8de2e9ef-fc82-4960-a691-d4a98aa19673")
    );

    private const int NamingStressItemId = 8411;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };

        var seedResult = await MssqlProfileIfMatchEtagTestSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            DocumentUuid,
            "mssql-profile-if-match-seed"
        );

        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var readableProfileProjectionContext =
            MssqlProfileIfMatchEtagTestSupport.CreateReadableProfileProjectionContext();

        _unprofiledGetBeforeUpdate = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-profile-if-match-get-before"
        );
        _profiledGetBeforeUpdate = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-profile-if-match-get-before-profiled",
            readableProfileProjectionContext
        );
        _unprofiledQueryBeforeUpdate = await MssqlProfileIfMatchEtagTestSupport.ExecuteQueryAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            "mssql-profile-if-match-query-before"
        );
        _profiledQueryBeforeUpdate = await MssqlProfileIfMatchEtagTestSupport.ExecuteQueryAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            "mssql-profile-if-match-query-before-profiled",
            readableProfileProjectionContext
        );

        var profiledGetBeforeUpdateDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _profiledGetBeforeUpdate
        );
        var currentProfiledEtag = MssqlProfileIfMatchEtagTestSupport.GetEtag(profiledGetBeforeUpdateDocument);
        var profiledPutBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
        };

        _profiledPutResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            profiledPutBody,
            "mssql-profile-if-match-profiled-put",
            currentProfiledEtag
        );

        _unprofiledGetAfterUpdate = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-profile-if-match-get-after"
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
    public void It_serves_profile_sensitive_etags_consistent_within_each_representation()
    {
        var unprofiledGetDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _unprofiledGetBeforeUpdate
        );
        var profiledGetDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _profiledGetBeforeUpdate
        );
        var unprofiledQueryDocument = MssqlProfileIfMatchEtagTestSupport.GetSingleDocument(
            _unprofiledQueryBeforeUpdate
        );
        var profiledQueryDocument = MssqlProfileIfMatchEtagTestSupport.GetSingleDocument(
            _profiledQueryBeforeUpdate
        );

        profiledGetDocument["order"].Should().BeNull();
        profiledQueryDocument["order"].Should().BeNull();

        var unprofiledEtag = MssqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetDocument);
        var profiledEtag = MssqlProfileIfMatchEtagTestSupport.GetEtag(profiledGetDocument);

        // GET and query agree within the same representation (same profileCode).
        MssqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledQueryDocument).Should().Be(unprofiledEtag);
        MssqlProfileIfMatchEtagTestSupport.GetEtag(profiledQueryDocument).Should().Be(profiledEtag);

        // But the profiled representation is a distinct strong validator (different profileCode),
        // while both reflect the same underlying ContentVersion.
        profiledEtag.Should().NotBe(unprofiledEtag);
        ContentVersionComponent(profiledEtag).Should().Be(ContentVersionComponent(unprofiledEtag));
    }

    [Test]
    public void It_returns_the_committed_profiled_etag_from_the_profiled_put()
    {
        var updateSuccess = _profiledPutResult.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        var unprofiledGetAfterUpdateDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _unprofiledGetAfterUpdate
        );
        var unprofiledEtagAfterUpdate = MssqlProfileIfMatchEtagTestSupport.GetEtag(
            unprofiledGetAfterUpdateDocument
        );

        updateSuccess.ETag.Should().NotBeNull();
        // The profiled PUT's etag reflects the same post-update ContentVersion as the unprofiled GET,
        // but carries the profile's distinct profileCode.
        ContentVersionComponent(updateSuccess.ETag!)
            .Should()
            .Be(ContentVersionComponent(unprofiledEtagAfterUpdate));
        updateSuccess.ETag.Should().NotBe(unprofiledEtagAfterUpdate);
    }

    private static string ContentVersionComponent(string etag) => etag.Split('-')[0];
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Profiled_Put_With_A_Stale_Profiled_Etag_After_A_Hidden_Field_Change
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("3ebd79f8-2b42-43e2-b867-1964779baf99")
    );

    private const int NamingStressItemId = 8422;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GetResult _unprofiledGetBeforeHiddenChange = null!;
    private GetResult _profiledGetBeforeHiddenChange = null!;
    private GetResult _unprofiledGetAfterHiddenChange = null!;
    private GetResult _profiledGetAfterHiddenChange = null!;
    private UpdateResult _staleProfiledPutResult = null!;

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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };

        var seedResult = await MssqlProfileIfMatchEtagTestSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            NamingStressItemId,
            seedBody,
            DocumentUuid,
            "mssql-profile-if-match-stale-seed"
        );

        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var readableProfileProjectionContext =
            MssqlProfileIfMatchEtagTestSupport.CreateReadableProfileProjectionContext();

        _unprofiledGetBeforeHiddenChange = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-profile-if-match-stale-get-before"
        );
        _profiledGetBeforeHiddenChange = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-profile-if-match-stale-get-before-profiled",
            readableProfileProjectionContext
        );

        var hiddenFieldChangeBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 43,
        };

        var hiddenFieldChangeResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteUnprofiledUpdateAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            hiddenFieldChangeBody,
            "mssql-profile-if-match-hidden-field-change"
        );

        hiddenFieldChangeResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

        _unprofiledGetAfterHiddenChange = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-profile-if-match-stale-get-after"
        );
        _profiledGetAfterHiddenChange = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-profile-if-match-stale-get-after-profiled",
            readableProfileProjectionContext
        );

        var staleProfiledEtag = MssqlProfileIfMatchEtagTestSupport.GetEtag(
            MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(_profiledGetBeforeHiddenChange)
        );
        var staleProfiledPutBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
        };

        _staleProfiledPutResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            staleProfiledPutBody,
            "mssql-profile-if-match-stale-profiled-put",
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
        var unprofiledGetBeforeHiddenChangeDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _unprofiledGetBeforeHiddenChange
        );
        var profiledGetBeforeHiddenChangeDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _profiledGetBeforeHiddenChange
        );
        var unprofiledGetAfterHiddenChangeDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _unprofiledGetAfterHiddenChange
        );
        var profiledGetAfterHiddenChangeDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _profiledGetAfterHiddenChange
        );

        profiledGetBeforeHiddenChangeDocument["order"].Should().BeNull();
        profiledGetAfterHiddenChangeDocument["order"].Should().BeNull();

        // The profiled representation is a distinct strong validator from the unprofiled one
        // (different profileCode), before and after the hidden change.
        MssqlProfileIfMatchEtagTestSupport
            .GetEtag(profiledGetBeforeHiddenChangeDocument)
            .Should()
            .NotBe(MssqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetBeforeHiddenChangeDocument));

        // A hidden persisted field change bumps ContentVersion, so the profiled etag changes too,
        // while remaining distinct from the unprofiled representation.
        MssqlProfileIfMatchEtagTestSupport
            .GetEtag(profiledGetAfterHiddenChangeDocument)
            .Should()
            .NotBe(MssqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetAfterHiddenChangeDocument))
            .And.NotBe(MssqlProfileIfMatchEtagTestSupport.GetEtag(profiledGetBeforeHiddenChangeDocument));
    }

    [Test]
    public void It_returns_412_for_a_profiled_put_using_the_stale_profiled_etag() =>
        _staleProfiledPutResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Unprofiled_Put_Using_The_Current_Profiled_Get_Etag
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("7d4a3b2c-0e5f-4a9b-9c3d-8f2e1b6a5d44")
    );

    private const int NamingStressItemId = 7444;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _unprofiledPutResult = null!;

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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };

        (
            await MssqlProfileIfMatchEtagTestSupport.SeedAsync(
                _serviceProvider,
                _database,
                _mappingSet,
                NamingStressItemId,
                seedBody,
                DocumentUuid,
                "mssql-unprofiled-put-profiled-etag-seed"
            )
        )
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();

        var profiledGet = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-unprofiled-put-profiled-etag-get",
            MssqlProfileIfMatchEtagTestSupport.CreateReadableProfileProjectionContext()
        );

        var profiledEtag = MssqlProfileIfMatchEtagTestSupport.GetEtag(
            MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(profiledGet)
        );

        var unprofiledPutBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
            ["order"] = 42,
        };

        _unprofiledPutResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteUnprofiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            unprofiledPutBody,
            "mssql-unprofiled-put-profiled-etag-put",
            profiledEtag
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
    public void It_accepts_the_profiled_get_etag_for_an_unprofiled_put() =>
        _unprofiledPutResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
}

// ─────────────────────────────────────────────────────────────────────────────
//  RFC 7232 wildcard (If-Match: *) end-to-end behavior against a real database.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Wildcard_IfMatch_Put_Against_An_Existing_Document
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("c2d3e4f5-0001-4b2c-8d3e-2b3c4d5e6f01")
    );

    private const int NamingStressItemId = 8511;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private string _etagBeforeUpdate = null!;
    private UpdateResult _wildcardPutResult = null!;
    private GetResult _getAfterUpdate = null!;

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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };

        (
            await MssqlProfileIfMatchEtagTestSupport.SeedAsync(
                _serviceProvider,
                _database,
                _mappingSet,
                NamingStressItemId,
                seedBody,
                DocumentUuid,
                "mssql-wildcard-put-existing-seed"
            )
        )
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();

        var getBeforeUpdate = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-wildcard-put-existing-get-before"
        );
        _etagBeforeUpdate = MssqlProfileIfMatchEtagTestSupport.GetEtag(
            MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(getBeforeUpdate)
        );

        var putBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
            ["order"] = 42,
        };

        _wildcardPutResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteUnprofiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            putBody,
            "mssql-wildcard-put-existing-put",
            "*"
        );

        _getAfterUpdate = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-wildcard-put-existing-get-after"
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
    public void It_succeeds_for_a_wildcard_put_against_an_existing_document() =>
        _wildcardPutResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_bumps_the_content_version_and_reflects_the_update()
    {
        var afterDocument = MssqlProfileIfMatchEtagTestSupport.GetSuccessDocument(_getAfterUpdate);
        afterDocument["shortName"]!.GetValue<string>().Should().Be("Updated");
        MssqlProfileIfMatchEtagTestSupport.GetEtag(afterDocument).Should().NotBe(_etagBeforeUpdate);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Wildcard_IfMatch_Delete_Against_An_Existing_Document
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("c2d3e4f5-0002-4b2c-8d3e-2b3c4d5e6f02")
    );

    private const int NamingStressItemId = 8522;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private DeleteResult _wildcardDeleteResult = null!;
    private GetResult _getAfterDelete = null!;

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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Original",
            ["order"] = 42,
        };

        (
            await MssqlProfileIfMatchEtagTestSupport.SeedAsync(
                _serviceProvider,
                _database,
                _mappingSet,
                NamingStressItemId,
                seedBody,
                DocumentUuid,
                "mssql-wildcard-delete-existing-seed"
            )
        )
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();

        _wildcardDeleteResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteDeleteAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-wildcard-delete-existing-delete",
            "*"
        );

        _getAfterDelete = await MssqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-wildcard-delete-existing-get-after"
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
    public void It_succeeds_for_a_wildcard_delete_against_an_existing_document() =>
        _wildcardDeleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();

    [Test]
    public void It_removes_the_document() =>
        _getAfterDelete.Should().BeOfType<GetResult.GetFailureNotExists>();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Wildcard_IfMatch_Put_Against_A_Missing_Document
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("c2d3e4f5-0003-4b2c-8d3e-2b3c4d5e6f03")
    );

    private const int NamingStressItemId = 8533;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _wildcardPutResult = null!;

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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        // Intentionally not seeded: the target document does not exist.
        var putBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
            ["order"] = 42,
        };

        _wildcardPutResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteUnprofiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            putBody,
            "mssql-wildcard-put-missing-put",
            "*"
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
    public void It_returns_412_and_not_404_for_a_wildcard_put_against_a_missing_document() =>
        _wildcardPutResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Wildcard_IfMatch_Delete_Against_A_Missing_Document
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("c2d3e4f5-0004-4b2c-8d3e-2b3c4d5e6f04")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private DeleteResult _wildcardDeleteResult = null!;

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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        // Intentionally not seeded: the target document does not exist.
        _wildcardDeleteResult = await MssqlProfileIfMatchEtagTestSupport.ExecuteDeleteAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "mssql-wildcard-delete-missing-delete",
            "*"
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
    public void It_returns_412_and_not_404_for_a_wildcard_delete_against_a_missing_document() =>
        _wildcardDeleteResult.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Wildcard_IfMatch_Post_Resolving_To_Insert
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("c2d3e4f5-0005-4b2c-8d3e-2b3c4d5e6f05")
    );

    private const int NamingStressItemId = 8555;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _wildcardPostResult = null!;

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
        _serviceProvider = MssqlProfileIfMatchEtagTestSupport.CreateServiceProvider();

        // Intentionally not seeded: the POST resolves to an insert of a brand-new document.
        var postBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Created",
            ["order"] = 99,
        };

        _wildcardPostResult = await MssqlProfileIfMatchEtagTestSupport.ExecutePostAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            postBody,
            "mssql-wildcard-post-insert-post",
            "*"
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
    public void It_returns_412_for_a_wildcard_post_resolving_to_insert() =>
        _wildcardPostResult.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
}
