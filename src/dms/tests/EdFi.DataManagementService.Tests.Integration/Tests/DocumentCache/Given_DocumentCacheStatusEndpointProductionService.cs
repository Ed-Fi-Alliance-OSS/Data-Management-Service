// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Tests.Integration.Tests.DocumentCache;

[TestFixture]
[NonParallelizable]
[Category("DocumentCacheStatus")]
[Category("DocumentCacheStatusEndpointProductionService")]
[Category("PostgresqlIntegration")]
public class Given_DocumentCacheStatusEndpointProductionService
{
    private const string RequiredRole = "dms-document-cache-operator";
    private const string RoleClaimType = "role";
    private const string ValidBearerToken = "valid-status-token";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StandardJsonContentType = "application/json";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";
    private const long EmptyTargetDataStoreId = 1;
    private const long QueuedTargetDataStoreId = 2;
    private const long UnreachableTargetDataStoreId = 3;
    private const string UnreachableSecret = "status-endpoint-secret-value";
    private const string UnreachableDatabaseName = "status_endpoint_secret_database";
    private const string UnreachableApplicationName = "raw-provider-status-app";
    private const string RawProviderParseText = "Couldn't set port";

    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private FixtureContext _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase? _emptyTargetDatabase;
    private PostgresqlGeneratedDdlTestDatabase? _queuedTargetDatabase;
    private string? _startupStatusFilePath;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        try
        {
            _ = BaselineDatabaseConfiguration.DatabaseConnectionString;
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore(
                "DatabaseConnection is not configured (set ConnectionStrings__DatabaseConnection or add it to appsettings.Test.json); skipping PostgreSQL document-cache status endpoint integration tests."
            );
        }

        _fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);
        _baseline = await PostgresqlBaselineCache.CreateOrGetAsync(_fixture);
    }

    [SetUp]
    public async Task SetUp()
    {
        _startupStatusFilePath = Path.Combine(
            Path.GetTempPath(),
            $"api-int-document-cache-status-{Guid.NewGuid():N}.json"
        );
        _emptyTargetDatabase = await _baseline.CreateIsolatedDatabaseAsync();
        _queuedTargetDatabase = await _baseline.CreateIsolatedDatabaseAsync();

        await SetTrackingLifecycleAsync(_emptyTargetDatabase, cacheAheadRecoveryRequired: false);
        await SetTrackingLifecycleAsync(_queuedTargetDatabase, cacheAheadRecoveryRequired: false);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_emptyTargetDatabase is not null)
        {
            await _emptyTargetDatabase.DisposeAsync();
            _emptyTargetDatabase = null;
        }

        if (_queuedTargetDatabase is not null)
        {
            await _queuedTargetDatabase.DisposeAsync();
            _queuedTargetDatabase = null;
        }

        if (_startupStatusFilePath is not null && File.Exists(_startupStatusFilePath))
        {
            try
            {
                File.Delete(_startupStatusFilePath);
            }
            catch
            {
                // Best-effort cleanup; never mask test failures.
            }
            _startupStatusFilePath = null;
        }
    }

    [Test]
    public async Task It_returns_real_provider_backed_multi_target_status_without_leaking_provider_details()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ValidBearerToken
        );

        factory
            .Services.GetRequiredService<IDocumentCacheStatusService>()
            .GetType()
            .Name.Should()
            .Be("DocumentCacheStatusService");

        await factory
            .Services.GetRequiredService<IDocumentCacheProjectionSupervisor>()
            .RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        await InsertQueuedProjectionWorkAsync(_queuedTargetDatabase!);

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode json = JsonNode.Parse(content)!;
        json["contractVersion"]!.GetValue<int>().Should().Be(1);
        json["targets"]!.AsArray().Count.Should().Be(3);

        JsonNode emptyTarget = TargetByDataStoreId(json, EmptyTargetDataStoreId);
        emptyTarget["provider"]!.GetValue<string>().Should().Be("postgresql");
        emptyTarget["durableObservedAt"].Should().NotBeNull();
        emptyTarget["lifecycle"]!["state"]!.GetValue<string>().Should().Be("tracking");
        emptyTarget["queueSummary"]!["presence"]!.GetValue<string>().Should().Be("empty");
        emptyTarget["caughtUp"]!["status"]!.GetValue<string>().Should().Be("caughtUp");

        JsonNode queuedTarget = TargetByDataStoreId(json, QueuedTargetDataStoreId);
        queuedTarget["provider"]!.GetValue<string>().Should().Be("postgresql");
        queuedTarget["durableObservedAt"].Should().NotBeNull();
        queuedTarget["lifecycle"]!["state"]!.GetValue<string>().Should().Be("tracking");
        queuedTarget["queueSummary"]!["presence"]!.GetValue<string>().Should().Be("notEmpty");
        queuedTarget["queueSummary"]!["oldestWorkFirstEnqueuedAt"].Should().NotBeNull();
        queuedTarget["queueSummary"]!["oldestWorkAgeSeconds"]!.GetValue<double>().Should().BeGreaterThan(0);
        queuedTarget["caughtUp"]!["status"]!.GetValue<string>().Should().Be("notCaughtUp");
        queuedTarget["caughtUp"]!["reason"]!.GetValue<string>().Should().Be("queueNotEmpty");

        JsonNode unreachableTarget = TargetByDataStoreId(json, UnreachableTargetDataStoreId);
        unreachableTarget["durableObservedAt"].Should().BeNull();
        unreachableTarget["operationalHealth"]!["status"]!.GetValue<string>().Should().NotBe("operational");

        content
            .Should()
            .NotContain(_emptyTargetDatabase!.ConnectionString)
            .And.NotContain(_queuedTargetDatabase!.ConnectionString)
            .And.NotContain(UnreachableSecret)
            .And.NotContain(UnreachableDatabaseName)
            .And.NotContain(UnreachableApplicationName)
            .And.NotContain("not-a-number")
            .And.NotContain(RawProviderParseText);
    }

    [Test]
    public async Task It_keeps_normal_api_routing_open_when_later_projection_work_makes_cdc_status_not_caught_up()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ValidBearerToken
        );

        await factory
            .Services.GetRequiredService<IDocumentCacheProjectionSupervisor>()
            .RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        using HttpResponseMessage initialStatusResponse = await client.GetAsync("/health/document-cache");
        string initialStatusContent = await initialStatusResponse.Content.ReadAsStringAsync();
        initialStatusResponse.StatusCode.Should().Be(HttpStatusCode.OK, initialStatusContent);
        JsonNode initialStatus = JsonNode.Parse(initialStatusContent)!;
        TargetByDataStoreId(initialStatus, EmptyTargetDataStoreId)["caughtUp"]!["status"]!
            .GetValue<string>()
            .Should()
            .Be("caughtUp");

        string studentUniqueId = $"cdc-routing-open-{Guid.NewGuid():N}"[..32];
        string locationPath = await PostStudentAsync(client, studentUniqueId, "Routing Open");
        DocumentMetadata metadata = await ReadDocumentMetadataAsync(_emptyTargetDatabase!, locationPath);
        (await CountProjectionWorkRowsAsync(_emptyTargetDatabase!, metadata.DocumentId))
            .Should()
            .Be(
                1,
                "a Tracking lifecycle with the projector waiting for its next poll must still enqueue API writes"
            );
        await UpsertStudentCacheRowAsync(
            _emptyTargetDatabase!,
            metadata,
            studentUniqueId,
            "Stale Routing Open",
            contentVersionOverride: metadata.ContentVersion - 1
        );

        using HttpResponseMessage laterStatusResponse = await client.GetAsync("/health/document-cache");
        string laterStatusContent = await laterStatusResponse.Content.ReadAsStringAsync();
        laterStatusResponse.StatusCode.Should().Be(HttpStatusCode.OK, laterStatusContent);
        JsonNode laterStatus = JsonNode.Parse(laterStatusContent)!;
        JsonNode target = TargetByDataStoreId(laterStatus, EmptyTargetDataStoreId);
        target["queueSummary"]!["presence"]!.GetValue<string>().Should().Be("notEmpty");
        target["executionState"]!["status"]!.GetValue<string>().Should().Be("waitingForPoll");
        target["caughtUp"]!["status"]!.GetValue<string>().Should().Be("notCaughtUp");
        target["caughtUp"]!["reason"]!.GetValue<string>().Should().Be("queueNotEmpty");

        JsonObject student = await GetJsonObjectAsync(client, locationPath);

        student["firstName"]!.GetValue<string>().Should().Be("Routing Open");
    }

    [Test]
    public async Task It_reports_disabled_lifecycle_and_bypasses_cache_without_blocking_api_writes()
    {
        await SetDisabledLifecycleAsync(_emptyTargetDatabase!, cacheAheadRecoveryRequired: false);
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ValidBearerToken
        );

        await factory
            .Services.GetRequiredService<IDocumentCacheProjectionSupervisor>()
            .RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        using HttpResponseMessage statusResponse = await client.GetAsync("/health/document-cache");
        string statusContent = await statusResponse.Content.ReadAsStringAsync();
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK, statusContent);
        JsonNode target = TargetByDataStoreId(JsonNode.Parse(statusContent)!, EmptyTargetDataStoreId);
        target["lifecycle"]!["state"]!.GetValue<string>().Should().Be("disabled");
        target["operationalHealth"]!["status"]!.GetValue<string>().Should().Be("nonOperational");
        target["operationalHealth"]!["reason"]!.GetValue<string>().Should().Be("lifecycleDisabled");

        string studentUniqueId = $"cdc-disabled-{Guid.NewGuid():N}"[..32];
        string locationPath = await PostStudentAsync(client, studentUniqueId, "Lifecycle Disabled");
        DocumentMetadata metadata = await ReadDocumentMetadataAsync(_emptyTargetDatabase!, locationPath);
        (await CountProjectionWorkRowsAsync(_emptyTargetDatabase!, metadata.DocumentId))
            .Should()
            .Be(0, "disabled lifecycle should commit canonical API data without enqueuing projection work");

        await UpsertStudentCacheRowAsync(_emptyTargetDatabase!, metadata, studentUniqueId, "Cached Disabled");

        JsonObject student = await GetJsonObjectAsync(client, locationPath);
        student["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Lifecycle Disabled", "read acceleration must bypass cache rows while lifecycle is disabled");
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        string unreachableConnectionString =
            $"Host=localhost;Port=not-a-number;Username=status_user;Password={UnreachableSecret};Database={UnreachableDatabaseName};Application Name={UnreachableApplicationName}";

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:UseApiSchemaPath"] = "true",
                            ["AppSettings:ApiSchemaPath"] = _fixture.ApiSchemaDirectory,
                            ["AppSettings:StartupStatusFilePath"] = _startupStatusFilePath,
                            ["AppSettings:Datastore"] = "postgresql",
                            ["AppSettings:BypassAuthorization"] = "true",
                            ["DataManagement:DocumentCache:ReadAcceleration:Enabled"] = "true",
                            ["DataManagement:DocumentCache:Projector:PollInterval"] = "01:00:00",
                            ["DataManagement:DocumentCache:Projector:MaxConcurrentTargets"] = "2",
                            ["DataManagement:DocumentCache:Status:RequiredRole"] = RequiredRole,
                            ["DataManagement:DocumentCache:Status:StatusObservationTimeout"] = "00:00:05",
                            ["DataManagement:DocumentCache:Status:EndpointTimeout"] = "00:00:30",
                            ["DataManagement:DocumentCache:Targets:0:TenantKey"] = string.Empty,
                            ["DataManagement:DocumentCache:Targets:0:DataStoreId"] =
                                EmptyTargetDataStoreId.ToString(),
                            ["DataManagement:DocumentCache:Targets:1:TenantKey"] = string.Empty,
                            ["DataManagement:DocumentCache:Targets:1:DataStoreId"] =
                                QueuedTargetDataStoreId.ToString(),
                            ["DataManagement:DocumentCache:Targets:2:TenantKey"] = string.Empty,
                            ["DataManagement:DocumentCache:Targets:2:DataStoreId"] =
                                UnreachableTargetDataStoreId.ToString(),
                            ["JwtAuthentication:RoleClaimType"] = RoleClaimType,
                            ["JwtAuthentication:ClientRole"] = "legacy-service",
                            ["ConfigurationServiceSettings:BaseUrl"] = "http://localhost/test-cms",
                            ["ConfigurationServiceSettings:ClientId"] = "test-cms-client",
                            ["ConfigurationServiceSettings:ClientSecret"] = "test-cms-secret",
                            ["ConfigurationServiceSettings:Scope"] = "edfi_admin_api/full_access",
                        }
                    );
                }
            );

            builder.ConfigureServices(services =>
            {
                ExternalDoublesRegistration.RegisterAll(
                    services,
                    _fixture,
                    _emptyTargetDatabase!.ConnectionString,
                    new AllowAllClaimSetProvider(_fixture),
                    [],
                    relationalProviderToken: RelationalProviderToken.Postgresql
                );

                services.RemoveAll<IDataStoreProvider>();
                services.AddSingleton<IDataStoreProvider>(
                    FakeDataStoreProvider.WithInstances([
                        new FakeDataStoreDefinition(
                            EmptyTargetDataStoreId,
                            _emptyTargetDatabase!.ConnectionString,
                            RelationalProviderToken.Postgresql
                        ),
                        new FakeDataStoreDefinition(
                            QueuedTargetDataStoreId,
                            _queuedTargetDatabase!.ConnectionString,
                            RelationalProviderToken.Postgresql
                        ),
                        new FakeDataStoreDefinition(
                            UnreachableTargetDataStoreId,
                            unreachableConnectionString,
                            RelationalProviderToken.Postgresql
                        ),
                    ])
                );
                services.RemoveAll<IJwtValidationService>();
                services.AddSingleton<IJwtValidationService>(new StatusJwtValidationService());
            });
        });
    }

    private static JsonNode TargetByDataStoreId(JsonNode response, long dataStoreId) =>
        response["targets"]!
            .AsArray()
            .Single(target => target!["targetKey"]!["dataStoreId"]!.GetValue<long>() == dataStoreId)!;

    private static async Task<string> PostStudentAsync(
        HttpClient client,
        string studentUniqueId,
        string firstName
    )
    {
        var payload = new JsonObject { ["studentUniqueId"] = studentUniqueId, ["firstName"] = firstName };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage createResponse = await client.PostAsync(StudentsEndpoint, content);
        string createBody = await createResponse.Content.ReadAsStringAsync();

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();

        return createResponse.Headers.Location!.PathAndQuery;
    }

    private static async Task<JsonObject> GetJsonObjectAsync(HttpClient client, string locationPath)
    {
        using HttpResponseMessage response = await client.GetAsync(locationPath);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be(StandardJsonContentType);

        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task SetTrackingLifecycleAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        bool cacheAheadRecoveryRequired
    )
    {
        await SetLifecycleAsync(database, "Tracking", cacheAheadRecoveryRequired);
    }

    private static async Task SetDisabledLifecycleAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        bool cacheAheadRecoveryRequired
    )
    {
        await SetLifecycleAsync(database, "Disabled", cacheAheadRecoveryRequired);
    }

    private static async Task SetLifecycleAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string lifecycleState,
        bool cacheAheadRecoveryRequired
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("lifecycleState", lifecycleState),
            new NpgsqlParameter("cacheAheadRecoveryRequired", cacheAheadRecoveryRequired)
        );
    }

    private static async Task<DocumentMetadata> ReadDocumentMetadataAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string locationPath
    )
    {
        var documentUuid = Guid.Parse(locationPath.Split('/')[^1]);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            SELECT d."DocumentId",
                   d."DocumentUuid",
                   d."ContentVersion",
                   d."ContentLastModifiedAt",
                   rk."ProjectName",
                   rk."ResourceName",
                   rk."ResourceVersion"
            FROM "dms"."Document" d
            INNER JOIN "dms"."ResourceKey" rk
                ON rk."ResourceKeyId" = d."ResourceKeyId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = documentUuid }
        );

        IReadOnlyDictionary<string, object?> row = rows.Should().ContainSingle().Subject;
        return new(
            Convert.ToInt64(row["DocumentId"], CultureInfo.InvariantCulture),
            (Guid)row["DocumentUuid"]!,
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            ToUtcDateTimeOffset(row["ContentLastModifiedAt"]!),
            (string)row["ProjectName"]!,
            (string)row["ResourceName"]!,
            (string)row["ResourceVersion"]!
        );
    }

    private static async Task<int> CountProjectionWorkRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        long count = await database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );
        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }

    private static async Task UpsertStudentCacheRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentMetadata metadata,
        string studentUniqueId,
        string firstName,
        long? contentVersionOverride = null
    )
    {
        long contentVersion = contentVersionOverride ?? metadata.ContentVersion;
        var documentJson = new JsonObject
        {
            ["id"] = metadata.DocumentUuid.ToString(),
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = firstName,
            ["_lastModifiedDate"] = FormatLastModifiedDate(metadata.ContentLastModifiedAt),
        };

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DocumentCache" (
                "DocumentId",
                "DocumentUuid",
                "ProjectName",
                "ResourceName",
                "ResourceVersion",
                "ContentVersion",
                "StreamEtag",
                "LastModifiedAt",
                "DocumentJson",
                "ComputedAt"
            )
            VALUES (
                @documentId,
                @documentUuid,
                @projectName,
                @resourceName,
                @resourceVersion,
                @contentVersion,
                @streamEtag,
                @lastModifiedAt,
                CAST(@documentJson AS jsonb),
                @computedAt
            )
            ON CONFLICT ("DocumentId") DO UPDATE
            SET "DocumentUuid" = EXCLUDED."DocumentUuid",
                "ProjectName" = EXCLUDED."ProjectName",
                "ResourceName" = EXCLUDED."ResourceName",
                "ResourceVersion" = EXCLUDED."ResourceVersion",
                "ContentVersion" = EXCLUDED."ContentVersion",
                "StreamEtag" = EXCLUDED."StreamEtag",
                "LastModifiedAt" = EXCLUDED."LastModifiedAt",
                "DocumentJson" = EXCLUDED."DocumentJson",
                "ComputedAt" = EXCLUDED."ComputedAt";
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = metadata.DocumentId },
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = metadata.DocumentUuid },
            new NpgsqlParameter("projectName", metadata.ProjectName),
            new NpgsqlParameter("resourceName", metadata.ResourceName),
            new NpgsqlParameter("resourceVersion", metadata.ResourceVersion),
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("streamEtag", $"status-cache-{contentVersion}"),
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz)
            {
                Value = metadata.ContentLastModifiedAt,
            },
            new NpgsqlParameter("documentJson", documentJson.ToJsonString()),
            new NpgsqlParameter("computedAt", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow }
        );
    }

    private static DateTimeOffset ToUtcDateTimeOffset(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"Unsupported ContentLastModifiedAt value type '{value.GetType().Name}'."
            ),
        };

    private static string FormatLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

    private static async Task InsertQueuedProjectionWorkAsync(PostgresqlGeneratedDdlTestDatabase database)
    {
        DateTimeOffset firstEnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        short resourceKeyId = await database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            ORDER BY "ResourceKeyId"
            LIMIT 1;
            """
        );

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            INSERT INTO "dms"."Document" (
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "ContentLastModifiedAt"
            )
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = 10L },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = firstEnqueuedAt }
        );

        long documentId = Convert.ToInt64(rows.Single()["DocumentId"]);
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DocumentProjectionWork" (
                "DocumentId",
                "RequiredContentVersion",
                "FirstEnqueuedAt",
                "LastEnqueuedAt"
            )
            VALUES (
                @documentId,
                @requiredContentVersion,
                @firstEnqueuedAt,
                @lastEnqueuedAt
            )
            ON CONFLICT ("DocumentId") DO UPDATE
            SET "RequiredContentVersion" = EXCLUDED."RequiredContentVersion",
                "FirstEnqueuedAt" = EXCLUDED."FirstEnqueuedAt",
                "LastEnqueuedAt" = EXCLUDED."LastEnqueuedAt";
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId },
            new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint) { Value = 10L },
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = firstEnqueuedAt },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz)
            {
                Value = firstEnqueuedAt.AddSeconds(5),
            }
        );
    }

    private sealed class StatusJwtValidationService : IJwtValidationService
    {
        private static readonly ClientAuthorizations _authorizations = new(
            ExternalDoublesConstants.SmokeToken,
            ExternalDoublesConstants.SmokeClientId,
            ExternalDoublesConstants.SmokeClaimSetName,
            [],
            [],
            [new DataStoreId(ExternalDoublesConstants.StableDataStoreId)]
        );
        private static readonly ClaimsPrincipal _principal = new(
            new ClaimsIdentity(
                [new Claim("client_id", "status-client"), new Claim(RoleClaimType, RequiredRole)],
                "test"
            )
        );

        public Task<(
            ClaimsPrincipal? Principal,
            ClientAuthorizations? ClientAuthorizations
        )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken)
        {
            bool isAcceptedToken =
                string.Equals(token, ValidBearerToken, StringComparison.Ordinal)
                || string.Equals(token, ExternalDoublesConstants.SmokeToken, StringComparison.Ordinal);
            ClaimsPrincipal? principal = isAcceptedToken ? _principal : null;
            ClientAuthorizations? authorizations = isAcceptedToken ? _authorizations : null;
            return Task.FromResult((principal, authorizations));
        }
    }

    private sealed record DocumentMetadata(
        long DocumentId,
        Guid DocumentUuid,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt,
        string ProjectName,
        string ResourceName,
        string ResourceVersion
    );
}
