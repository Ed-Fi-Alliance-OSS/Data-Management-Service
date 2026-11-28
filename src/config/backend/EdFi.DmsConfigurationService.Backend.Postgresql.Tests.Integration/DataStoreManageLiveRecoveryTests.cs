// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    // Startup/readiness probes are covered separately. Here the registered HTTP/handler/executor
    // pipeline uses the real trusted catalog and physical provisioner supplied by the existing lane.
    private WebApplicationFactory<Program> LiveRecoveryHost(
        Lane lane,
        string runtime,
        int? failFinalizationOf = null,
        bool delete = false
    ) =>
        DmsDataStoreProvisioningStartupTests.Given_managed_data_store_startup.Host(
            "true",
            true,
            target: "postgresql",
            catalog: "postgresql",
            overrides: new()
            {
                ["DatabaseSettings:DatabaseConnection"] = _database.ConnectionString,
                ["DatabaseSettings:EncryptionKey"] = PublicationOptions.Value.EncryptionKey.PadRight(32, '0'),
                ["DmsDataStoreProvisioning:AdminConnectionString"] = AdminConnectionString!,
                ["DataStoreManagement:RuntimeConnectionString"] = runtime,
            },
            configure: services =>
            {
                services.AddSingleton<IDmsDataStoreProvisioner>(lane.WithEngine(Engine()).Service);
                if (failFinalizationOf is { } id)
                {
                    services.AddSingleton<IJobFenceFactory>(
                        new LiveFinalizationFault(
                            new PostgresqlJobFenceFactory(PublicationOptions, PublicationTimings),
                            id,
                            delete ? "Deleted" : "Created"
                        )
                    );
                }
            }
        );

    // Inject after the real finalization writes, inside their real transaction. The executor
    // must persist a retry only after that transaction has rolled back.
    private sealed class LiveFinalizationFault(IJobFenceFactory inner, int id, string finalStatus)
        : IJobFenceFactory
    {
        public bool Fired { get; private set; }

        public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) =>
            new PublicationFenceDecorator(
                inner.Create(job, ownership),
                async (transaction, token) =>
                {
                    var status = await transaction.Connection!.ExecuteScalarAsync<string>(
                        new CommandDefinition(
                            """SELECT "Status" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                            new { Id = id },
                            transaction,
                            cancellationToken: token
                        )
                    );
                    if (status == finalStatus)
                    {
                        Fired = true;
                        throw new IOException("Injected managed finalization rollback.");
                    }
                }
            );
    }

    private async Task<ManagedDataStoreRow> LiveRow(int id)
    {
        await using var connection = await Open();
        return await connection.QuerySingleAsync<ManagedDataStoreRow>(
            """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
            new { Id = id }
        );
    }

    private static async Task<ClaimedJob> LiveClaim(
        WebApplicationFactory<Program> host,
        string jobId,
        int attempt
    )
    {
        var claim = (
            await host
                .Services.GetRequiredService<IJobLeaseRepository>()
                .ClaimNext("live-recovery", 300, 5, default)
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(jobId);
        claim.AttemptCount.Should().Be(attempt);
        return claim;
    }

    private async Task MakeLiveRetryDue(string jobId)
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "NextAttemptAt" = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE "JobId" = @JobId;""",
            new { JobId = jobId }
        );
    }

    [
        TestCase("Minimal"),
        TestCase("Sample"),
        Category("DataStoreManageLiveRecovery"),
        Category("DmsProvisioning")
    ]
    public async Task It_recovers_live_create_and_delete_across_hosts_and_reads_with_published_runtime_credentials(
        string templateName
    )
    {
        await VerifyLiveRecovery(templateName, false);
    }

    [
        TestCase("Minimal"),
        TestCase("Sample"),
        Category("DataStoreManageMixedRecovery"),
        Category("DmsProvisioning"),
        Category(DmsProvisioningLaneRequirement.CrossEngineCategory)
    ]
    public Task It_recovers_mixed_catalog_and_target_and_reads_with_published_runtime_credentials(
        string templateName
    ) => VerifyLiveRecovery(templateName, true);

    private async Task VerifyLiveRecovery(string templateName, bool mixedCatalog, bool runtimeWrite = false)
    {
        _ = RequireAdmissionEndpoint();
        if (!RealTemplatePackage.IsConfigured)
        {
            DmsProvisioningLaneRequirement.SkipLocallyOrFailInCi(
                "DMS_TEMPLATE_PACKAGE_DIRECTORY is required for live managed recovery."
            );
        }
        await using var alternate = mixedCatalog
            ? await ManagedDataStoreMixedCatalog.CreateAsync(true)
            : null;
        DmsDatabaseTemplate.TryFromWireValue(templateName, out var template).Should().BeTrue();
        var package = RealTemplatePackage.Find(
            "postgresql",
            templateName == "Minimal"
                ? RestoreManifest.MinimalTemplateKind
                : RestoreManifest.PopulatedTemplateKind
        );
        await using var lane = await Lane.BuildAsync(package, template!, new LaneHooks());
        string login = await CreateLiveRuntimeLogin();
        string runtime = LiveRuntimeConnection(login);
        async Task<DbConnection> OpenCatalog() =>
            alternate is null ? await Open() : await alternate.OpenAsync();
        async Task<ManagedDataStoreRow> ReadRow(int id)
        {
            await using var connection = await OpenCatalog();
            return await connection.QuerySingleAsync<ManagedDataStoreRow>(
                """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = id }
            );
        }
        async Task RetryDue(string jobId)
        {
            if (alternate is null)
            {
                await MakeLiveRetryDue(jobId);
                return;
            }
            await using var connection = await alternate.OpenAsync();
            await connection.ExecuteAsync(
                """UPDATE dmscs."Job" SET "NextAttemptAt" = DATEADD(second, -1, SYSUTCDATETIME()) WHERE "JobId" = @JobId;""",
                new { JobId = jobId }
            );
        }
        WebApplicationFactory<Program> Host(int? failFinalizationOf = null, bool delete = false)
        {
            if (alternate is null)
            {
                return LiveRecoveryHost(lane, runtime, failFinalizationOf, delete);
            }
            return LiveRecoveryHost(lane, runtime)
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("AppSettings:Datastore", alternate.Provider);
                    builder.UseSetting("DatabaseSettings:DatabaseConnection", alternate.ConnectionString);
                    if (failFinalizationOf is { } id)
                    {
                        builder.ConfigureServices(services =>
                            services.AddSingleton<IJobFenceFactory>(
                                new LiveFinalizationFault(
                                    new Backend.Mssql.Jobs.MssqlJobFenceFactory(
                                        Options.Create(
                                            new DatabaseOptions
                                            {
                                                DatabaseConnection = alternate.ConnectionString,
                                                EncryptionKey = PublicationOptions.Value.EncryptionKey,
                                            }
                                        ),
                                        PublicationTimings
                                    ),
                                    id,
                                    delete ? "Deleted" : "Created"
                                )
                            )
                        );
                    }
                });
        }
        TestDatabase? targetCleanup = null;
        try
        {
            int id;
            // Acceptance uses the real HTTP route, administrative snapshot and aggregate/job transaction.
            using (var host = Host())
            using (var client = ManagedClient(host))
            {
                if (alternate is not null)
                {
                    using var scope = host.Services.CreateScope();
                    scope
                        .ServiceProvider.GetRequiredService<Backend.Repositories.IDataStoreManageRepository>()
                        .Should()
                        .BeOfType<Backend.Mssql.Repositories.DataStoreManageRepository>();
                    host.Services.GetRequiredService<IJobFenceFactory>()
                        .Should()
                        .BeOfType<Backend.Mssql.Jobs.MssqlJobFenceFactory>();
                }
                var response = await client.PostAsJsonAsync(
                    "/v3/dataStores/manage",
                    new
                    {
                        name = "Live" + Guid.NewGuid().ToString("N")[..12],
                        databaseTemplate = templateName,
                    }
                );
                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
                id = int.Parse(response.Headers.Location!.Segments[^1]);
            }
            var accepted = await ReadRow(id);
            using var cacheHost = Host();
            using var cacheClient = ManagedClient(cacheHost);
            var clock = new FakeTimeProvider();
            var tokens = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() =>
                    tokens.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(new ConfigurationServiceTokenLease("test-authentication", 1));
            var discovery = new ConfigurationServiceDataStoreProvider(
                new ConfigurationServiceApiClient(cacheClient),
                tokens,
                new ConfigurationServiceContext("test-client", "test-secret", "edfi_admin_api/full_access"),
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(
                    PublicationOptions.Value.EncryptionKey,
                    allowLegacyConnectionStrings: false
                ),
                new CacheSettings { DataStoreCacheExpirationSeconds = 10 },
                clock
            );
            (await discovery.LoadDataStores()).Count.Should().Be(0);
            var aggregate = accepted.Restore(ProtectedDatabaseNames.Create("cms", []));
            aggregate.Endpoint.Provider.Should().Be("postgresql");
            var target = aggregate.TargetName;
            targetCleanup = new TestDatabase(target.Value);
            DmsInstanceIdentity createdInstance;

            using (var host = Host(id))
            using (var client = ManagedClient(host))
            {
                var claim = await LiveClaim(host, accepted.CreateJobId, 1);
                var result = await host
                    .Services.GetRequiredService<JobExecutor>()
                    .ExecuteAsync(claim, default);
                result.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
                ((LiveFinalizationFault)host.Services.GetRequiredService<IJobFenceFactory>())
                    .Fired.Should()
                    .BeTrue();
                var failed = await ReadRow(id);
                failed.Status.Should().Be("CreateFailed");
                failed.DataStoreId.Should().BeNull();
                failed
                    .Restore(ProtectedDatabaseNames.Create("cms", []))
                    .PinnedArtifact.Should()
                    .Be(lane.Request.PinnedArtifact);
                await using var catalog = await OpenCatalog();
                (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(0);
                createdInstance = (await lane.Engine.InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Present>()
                    .Subject.Instance;
            }

            // A new host and executor load the persisted pin and identity; no state is replayed in memory.
            await RetryDue(accepted.CreateJobId);
            int catalogId;
            using (var host = Host())
            using (var client = ManagedClient(host))
            {
                var claim = await LiveClaim(host, accepted.CreateJobId, 2);
                var result = await host
                    .Services.GetRequiredService<JobExecutor>()
                    .ExecuteAsync(claim, default);
                result.Outcome.Should().Be(JobExecutionOutcome.Completed);
                var created = await ReadRow(id);
                created.Status.Should().Be("Created");
                created.PinnedProvider.Should().Be("postgresql");
                if (alternate is not null)
                {
                    await using var unusedCatalog = await Open();
                    (
                        await unusedCatalog.ExecuteScalarAsync<int>(
                            """SELECT COUNT(*) FROM dmscs."DataStore";"""
                        )
                    )
                        .Should()
                        .Be(0);
                }
                catalogId = created.DataStoreId!.Value;
                created.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
                created
                    .Restore(ProtectedDatabaseNames.Create("cms", []))
                    .PinnedArtifact.Should()
                    .Be(lane.Request.PinnedArtifact);
                (await lane.Engine.InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Present>()
                    .Subject.Instance.Should()
                    .Be(createdInstance);
                await using var catalog = await OpenCatalog();
                (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(1);

                var ordinary = (
                    await client.GetFromJsonAsync<DataStoreResponse>($"/v3/dataStores/{catalogId}")
                )!;
                ordinary.Provider.Should().Be("postgresql");
                // Boolean checks avoid rendering a connection string/password on assertion failure.
                (ordinary.ConnectionString is not null)
                    .Should()
                    .BeTrue();
                await discovery.RefreshInstancesIfExpiredAsync();
                discovery.GetAll().Count.Should().Be(0);
                clock.Advance(TimeSpan.FromSeconds(11));
                await discovery.RefreshInstancesIfExpiredAsync();
                discovery.GetAll().Select(store => store.Id).Should().Equal(catalogId);
                var discovered = discovery.GetById(catalogId)!;
                discovered.RelationalProviderToken!.Value.Should().Be(ordinary.Provider);
                await VerifyLiveRuntime(
                    discovered.ConnectionString!,
                    login,
                    package.Manifest.EffectiveSchemaHash,
                    accepted.ExpectedSourceIdentity
                );
                if (runtimeWrite)
                {
                    await VerifyManagedRuntimeWrite(
                        discovered,
                        login,
                        package.Manifest.EffectiveSchemaHash,
                        package.Manifest.RelationalMappingVersion
                    );
                }
                (await client.DeleteAsync($"/v3/dataStores/manage/{id}"))
                    .StatusCode.Should()
                    .Be(HttpStatusCode.Accepted);
            }

            var deleting = await ReadRow(id);
            using (var host = Host(id, delete: true))
            using (var client = ManagedClient(host))
            {
                var claim = await LiveClaim(host, deleting.DeleteJobId!, 1);
                var result = await host
                    .Services.GetRequiredService<JobExecutor>()
                    .ExecuteAsync(claim, default);
                result.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
                ((LiveFinalizationFault)host.Services.GetRequiredService<IJobFenceFactory>())
                    .Fired.Should()
                    .BeTrue();
                var failed = await ReadRow(id);
                failed.Status.Should().Be("DeleteFailed");
                failed.DataStoreId.Should().Be(catalogId);
                (await lane.Engine.InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Absent>();
                await using var catalog = await OpenCatalog();
                (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(1);
            }

            await RetryDue(deleting.DeleteJobId!);
            using (var host = Host())
            using (var client = ManagedClient(host))
            {
                var claim = await LiveClaim(host, deleting.DeleteJobId!, 2);
                var result = await host
                    .Services.GetRequiredService<JobExecutor>()
                    .ExecuteAsync(claim, default);
                result.Outcome.Should().Be(JobExecutionOutcome.Completed);
                var deleted = await ReadRow(id);
                deleted.Status.Should().Be("Deleted");
                deleted.DataStoreId.Should().BeNull();
                deleted.HistoricalDataStoreId.Should().Be(catalogId);
                deleted.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
                (await lane.Engine.InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Absent>();
                await using var catalog = await OpenCatalog();
                (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(0);
            }
            // Deletion becomes visible to DMS on refresh, independently of the managed tombstone.
            discovery.GetAll().Select(store => store.Id).Should().Equal(catalogId);
            clock.Advance(TimeSpan.FromSeconds(11));
            await discovery.RefreshInstancesIfExpiredAsync();
            discovery.GetAll().Count.Should().Be(0);
            lane.OperationEntries.Should().BeEmpty();
        }
        finally
        {
            if (targetCleanup is not null)
            {
                await targetCleanup.DisposeAsync();
            }
            await DropLiveRuntimeLogin(login);
        }
    }

    // Test-only read principal established before provisioning; the product creates no principals/grants.
    private static async Task<string> CreateLiveRuntimeLogin()
    {
        await EnsureEnqueueOwnerRoleAsync();
        return await CreateLoginRoleAsync("managedruntime", "pg_read_all_data");
    }

    private static Task DropLiveRuntimeLogin(string login) => DropRoleAsync(login);

    private static string LiveRuntimeConnection(string login) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString!)
        {
            Username = login,
            Password = LoginRolePassword,
            Pooling = false,
        }.ConnectionString;

    private static async Task VerifyLiveRuntime(
        string connectionString,
        string login,
        string schema,
        Guid identity
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<string>("SELECT CURRENT_USER;")).Should().Be(login);
        (
            await connection.ExecuteScalarAsync<bool>(
                "SELECT rolsuper FROM pg_roles WHERE rolname = CURRENT_USER;"
            )
        )
            .Should()
            .BeFalse();
        (
            await connection.ExecuteScalarAsync<string>(
                """SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema";"""
            )
        )
            .Should()
            .Be(schema);
        (
            await connection.ExecuteScalarAsync<Guid>(
                """SELECT "SourceIdentity" FROM dms."DataStoreIdentity";"""
            )
        )
            .Should()
            .Be(identity);
    }
}
