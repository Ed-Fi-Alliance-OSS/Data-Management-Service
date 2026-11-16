// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using Dapper;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

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
            target: "mssql",
            catalog: "mssql",
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
                            new MssqlJobFenceFactory(PublicationOptions, PublicationTimings),
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
            """UPDATE dmscs."Job" SET "NextAttemptAt" = DATEADD(second, -1, SYSUTCDATETIME()) WHERE "JobId" = @JobId;""",
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
        _ = RequireAdmissionEndpoint();
        if (!RealTemplatePackage.IsConfigured)
        {
            DmsProvisioningLaneRequirement.SkipLocallyOrFailInCi(
                "DMS_TEMPLATE_PACKAGE_DIRECTORY is required for live managed recovery."
            );
        }
        DmsDatabaseTemplate.TryFromWireValue(templateName, out var template).Should().BeTrue();
        var package = RealTemplatePackage.Find(
            "mssql",
            templateName == "Minimal"
                ? RestoreManifest.MinimalTemplateKind
                : RestoreManifest.PopulatedTemplateKind
        );
        await using var lane = await Lane.BuildAsync(package, template!, new LaneHooks());
        string login = await CreateLiveRuntimeLogin();
        string runtime = LiveRuntimeConnection(login);
        TestDatabase? targetCleanup = null;
        try
        {
            int id;
            // Acceptance uses the real HTTP route, administrative snapshot and aggregate/job transaction.
            using (var host = LiveRecoveryHost(lane, runtime))
            using (var client = ManagedClient(host))
            {
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
            var accepted = await LiveRow(id);
            using var cacheHost = LiveRecoveryHost(lane, runtime);
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
            var target = aggregate.TargetName;
            targetCleanup = new TestDatabase(target.Value);
            DmsInstanceIdentity createdInstance;

            using (var host = LiveRecoveryHost(lane, runtime, id))
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
                var failed = await LiveRow(id);
                failed.Status.Should().Be("CreateFailed");
                failed.DataStoreId.Should().BeNull();
                failed
                    .Restore(ProtectedDatabaseNames.Create("cms", []))
                    .PinnedArtifact.Should()
                    .Be(lane.Request.PinnedArtifact);
                await using var catalog = await Open();
                (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(0);
                createdInstance = (await lane.Engine.InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Present>()
                    .Subject.Instance;
            }

            // A new host and executor load the persisted pin and identity; no state is replayed in memory.
            await MakeLiveRetryDue(accepted.CreateJobId);
            int catalogId;
            using (var host = LiveRecoveryHost(lane, runtime))
            using (var client = ManagedClient(host))
            {
                var claim = await LiveClaim(host, accepted.CreateJobId, 2);
                var result = await host
                    .Services.GetRequiredService<JobExecutor>()
                    .ExecuteAsync(claim, default);
                result.Outcome.Should().Be(JobExecutionOutcome.Completed);
                var created = await LiveRow(id);
                created.Status.Should().Be("Created");
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
                await using var catalog = await Open();
                (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(1);

                var ordinary = (
                    await client.GetFromJsonAsync<DataStoreResponse>($"/v3/dataStores/{catalogId}")
                )!;
                ordinary.Provider.Should().Be("sqlserver");
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
                (await client.DeleteAsync($"/v3/dataStores/manage/{id}"))
                    .StatusCode.Should()
                    .Be(HttpStatusCode.NoContent);
            }

            var deleting = await LiveRow(id);
            using (var host = LiveRecoveryHost(lane, runtime, id, delete: true))
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
                var failed = await LiveRow(id);
                failed.Status.Should().Be("DeleteFailed");
                failed.DataStoreId.Should().Be(catalogId);
                (await lane.Engine.InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Absent>();
                await using var catalog = await Open();
                (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(1);
            }

            await MakeLiveRetryDue(deleting.DeleteJobId!);
            using (var host = LiveRecoveryHost(lane, runtime))
            using (var client = ManagedClient(host))
            {
                var claim = await LiveClaim(host, deleting.DeleteJobId!, 2);
                var result = await host
                    .Services.GetRequiredService<JobExecutor>()
                    .ExecuteAsync(claim, default);
                result.Outcome.Should().Be(JobExecutionOutcome.Completed);
                var deleted = await LiveRow(id);
                deleted.Status.Should().Be("Deleted");
                deleted.DataStoreId.Should().BeNull();
                deleted.HistoricalDataStoreId.Should().Be(catalogId);
                deleted.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
                (await lane.Engine.InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Absent>();
                await using var catalog = await Open();
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

    // Test-only read principal established before provisioning; no sysadmin or DDL permission.
    // These server grants qualify read access to new targets on this isolated test server only.
    private static async Task<string> CreateLiveRuntimeLogin()
    {
        string login = await CreateLoginAsync("managedruntime");
        try
        {
            await ExecuteOnServerAsync(
                $"GRANT CONNECT ANY DATABASE TO [{login}]; GRANT SELECT ALL USER SECURABLES TO [{login}];"
            );
            return login;
        }
        catch
        {
            await DropLoginAsync(login);
            throw;
        }
    }

    private static Task DropLiveRuntimeLogin(string login) => DropLoginAsync(login);

    private static string LiveRuntimeConnection(string login) =>
        new SqlConnectionStringBuilder(AdminConnectionString!)
        {
            IntegratedSecurity = false,
            UserID = login,
            Password = LoginPassword,
            Pooling = false,
        }.ConnectionString;

    private static async Task VerifyLiveRuntime(
        string connectionString,
        string login,
        string schema,
        Guid identity
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<string>("SELECT SUSER_SNAME();")).Should().Be(login);
        (await connection.ExecuteScalarAsync<int>("SELECT IS_SRVROLEMEMBER('sysadmin');")).Should().Be(0);
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
