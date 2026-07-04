// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
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

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

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

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
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
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
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
            ResourceInfo: PostgresqlProfileRootTableOnlyMergeSupport.NamingStressItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootTableOnlyMergeSupport.CreateNamingStressDocumentInfo(
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

    private static void SetSelectedInstance(IServiceProvider serviceProvider, string connectionString)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlProfileIfMatchEtag",
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
    public void It_serves_profile_sensitive_etags_consistent_within_each_representation()
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

        var unprofiledEtag = PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetDocument);
        var profiledEtag = PostgresqlProfileIfMatchEtagTestSupport.GetEtag(profiledGetDocument);

        // GET and query agree within the same representation (same profileCode).
        PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledQueryDocument).Should().Be(unprofiledEtag);
        PostgresqlProfileIfMatchEtagTestSupport.GetEtag(profiledQueryDocument).Should().Be(profiledEtag);

        // But the profiled representation is a distinct strong validator (different profileCode),
        // while both reflect the same underlying ContentVersion.
        profiledEtag.Should().NotBe(unprofiledEtag);
        ContentVersionComponent(profiledEtag).Should().Be(ContentVersionComponent(unprofiledEtag));
    }

    [Test]
    public void It_returns_the_committed_profiled_etag_from_the_profiled_put()
    {
        var updateSuccess = _profiledPutResult.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        var unprofiledGetAfterUpdateDocument = PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(
            _unprofiledGetAfterUpdate
        );
        var unprofiledEtagAfterUpdate = PostgresqlProfileIfMatchEtagTestSupport.GetEtag(
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

        // The profiled representation is a distinct strong validator from the unprofiled one
        // (different profileCode), before and after the hidden change.
        PostgresqlProfileIfMatchEtagTestSupport
            .GetEtag(profiledGetBeforeHiddenChangeDocument)
            .Should()
            .NotBe(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetBeforeHiddenChangeDocument));

        // A hidden persisted field change bumps ContentVersion, so the profiled etag changes too,
        // while remaining distinct from the unprofiled representation.
        PostgresqlProfileIfMatchEtagTestSupport
            .GetEtag(profiledGetAfterHiddenChangeDocument)
            .Should()
            .NotBe(PostgresqlProfileIfMatchEtagTestSupport.GetEtag(unprofiledGetAfterHiddenChangeDocument))
            .And.NotBe(
                PostgresqlProfileIfMatchEtagTestSupport.GetEtag(profiledGetBeforeHiddenChangeDocument)
            );
    }

    [Test]
    public void It_returns_412_for_a_profiled_put_using_the_stale_profiled_etag() =>
        _staleProfiledPutResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Unprofiled_Put_Using_The_Current_Profiled_Get_Etag
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("6c3f2a1b-9d4e-4f8a-8b2c-7e1d0a5f4c33")
    );

    private const int NamingStressItemId = 7433;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _unprofiledPutResult = null!;

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

        (
            await PostgresqlProfileIfMatchEtagTestSupport.SeedAsync(
                _serviceProvider,
                _database,
                _mappingSet,
                NamingStressItemId,
                seedBody,
                DocumentUuid,
                "pg-unprofiled-put-profiled-etag-seed"
            )
        )
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();

        var profiledGet = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteGetByIdAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            DocumentUuid,
            "pg-unprofiled-put-profiled-etag-get",
            PostgresqlProfileIfMatchEtagTestSupport.CreateReadableProfileProjectionContext()
        );

        var profiledEtag = PostgresqlProfileIfMatchEtagTestSupport.GetEtag(
            PostgresqlProfileIfMatchEtagTestSupport.GetSuccessDocument(profiledGet)
        );

        // Full (unprofiled) replacement body, guarded by the etag captured from the profiled read.
        var unprofiledPutBody = new JsonObject
        {
            ["namingStressItemId"] = NamingStressItemId,
            ["shortName"] = "Updated",
            ["order"] = 42,
        };

        _unprofiledPutResult = await PostgresqlProfileIfMatchEtagTestSupport.ExecuteUnprofiledPutAsync(
            _serviceProvider,
            _database.ConnectionString,
            _mappingSet,
            NamingStressItemId,
            DocumentUuid,
            unprofiledPutBody,
            "pg-unprofiled-put-profiled-etag-put",
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
