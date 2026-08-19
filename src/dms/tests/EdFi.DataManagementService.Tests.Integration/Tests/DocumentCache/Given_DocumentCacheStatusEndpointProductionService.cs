// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
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

    private WebApplicationFactory<Program> CreateFactory()
    {
        string unreachableConnectionString =
            $"Host=localhost;Port=not-a-number;Username=status_user;Password={UnreachableSecret};Database={UnreachableDatabaseName};Application Name={UnreachableApplicationName}";

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:UseApiSchemaPath", "true");
            builder.UseSetting("AppSettings:ApiSchemaPath", _fixture.ApiSchemaDirectory);
            builder.UseSetting("AppSettings:StartupStatusFilePath", _startupStatusFilePath);
            builder.UseSetting("AppSettings:Datastore", "postgresql");
            builder.UseSetting("AppSettings:BypassAuthorization", "true");
            builder.UseSetting("DataManagement:DocumentCache:ReadAcceleration:Enabled", "true");
            builder.UseSetting("DataManagement:DocumentCache:Projector:PollInterval", "01:00:00");
            builder.UseSetting("DataManagement:DocumentCache:Projector:MaxConcurrentTargets", "2");
            builder.UseSetting("DataManagement:DocumentCache:Status:RequiredRole", RequiredRole);
            builder.UseSetting("DataManagement:DocumentCache:Targets:0:TenantKey", "");
            builder.UseSetting(
                "DataManagement:DocumentCache:Targets:0:DataStoreId",
                EmptyTargetDataStoreId.ToString()
            );
            builder.UseSetting("DataManagement:DocumentCache:Targets:1:TenantKey", "");
            builder.UseSetting(
                "DataManagement:DocumentCache:Targets:1:DataStoreId",
                QueuedTargetDataStoreId.ToString()
            );
            builder.UseSetting("DataManagement:DocumentCache:Targets:2:TenantKey", "");
            builder.UseSetting(
                "DataManagement:DocumentCache:Targets:2:DataStoreId",
                UnreachableTargetDataStoreId.ToString()
            );
            builder.UseSetting("JwtAuthentication:RoleClaimType", RoleClaimType);
            builder.UseSetting("JwtAuthentication:ClientRole", "legacy-service");
            builder.UseSetting("ConfigurationServiceSettings:BaseUrl", "http://localhost/test-cms");
            builder.UseSetting("ConfigurationServiceSettings:ClientId", "test-cms-client");
            builder.UseSetting("ConfigurationServiceSettings:ClientSecret", "test-cms-secret");
            builder.UseSetting("ConfigurationServiceSettings:Scope", "edfi_admin_api/full_access");

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

    private static async Task SetTrackingLifecycleAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        bool cacheAheadRecoveryRequired
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = 'Tracking',
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("cacheAheadRecoveryRequired", cacheAheadRecoveryRequired)
        );
    }

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
            );
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
            ClaimsPrincipal? principal = token == ValidBearerToken ? _principal : null;
            return Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>((principal, null));
        }
    }
}
