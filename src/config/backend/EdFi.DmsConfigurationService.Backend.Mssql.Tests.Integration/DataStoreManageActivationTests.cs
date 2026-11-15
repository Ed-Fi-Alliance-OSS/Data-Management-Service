// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private WebApplicationFactory<Program> ActivationHost(
        bool ready,
        IDmsDataStoreProvisioner? provisioner = null
    ) =>
        DmsDataStoreProvisioningStartupTests.Given_managed_data_store_startup.Host(
            "true",
            true,
            target: "mssql",
            catalog: "mssql",
            ready: ready,
            overrides: new()
            {
                ["DatabaseSettings:DatabaseConnection"] = _database.ConnectionString,
                ["DatabaseSettings:EncryptionKey"] = PublicationOptions.Value.EncryptionKey.PadRight(32, '0'),
                ["DataStoreManagement:RuntimeConnectionString"] =
                    "Server=tcp:db.example,1433;User ID=runtime;Password=test-only",
            },
            configure: services =>
            {
                if (provisioner is not null)
                {
                    services.AddSingleton(provisioner);
                }
            }
        );

    [TestCase(false), TestCase(true), Category("DataStoreManageActivation")]
    public async Task It_runs_registered_old_jobs_to_Error_under_closed_readiness_and_does_not_revive_them(
        bool delete
    )
    {
        int id;
        ClaimedJob claim;
        if (delete)
        {
            var seed = await SeedDeleteHandler();
            id = seed.Command.Id;
            claim = seed.Claim;
        }
        else
        {
            var seed = await SeedCreateHandler();
            id = seed.Command.Id;
            claim = seed.Claim;
        }
        using (var host = ActivationHost(false))
        {
            using var client = ManagedClient(host);
            var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, default);
            result.Outcome.Should().Be(JobExecutionOutcome.Failed);
            await using var connection = await Open();
            var row = await DeleteRow(connection, id);
            row["Status"].Should().Be(delete ? "DeleteError" : "CreateError");
            row["LastFailureCategory"].Should().Be("NotReady");
            (
                await connection.ExecuteScalarAsync<string>(
                    """SELECT "Status" FROM dmscs."Job" WHERE "Id" = @Id;""",
                    new { claim.Id }
                )
            )
                .Should()
                .Be("Error");
        }
        using var restarted = ActivationHost(true);
        using var restartedClient = ManagedClient(restarted);
        var leases = restarted.Services.GetRequiredService<IJobLeaseRepository>();
        (await leases.ClaimNext("restarted", 300, 5, default))
            .Should()
            .BeOfType<JobClaimResult.NoneAvailable>();
        await using var after = await Open();
        (await DeleteRow(after, id))["Status"].Should().Be(delete ? "DeleteError" : "CreateError");
    }

    [TestCase(false), TestCase(true), Category("DataStoreManageActivation")]
    public async Task It_schedules_a_transient_handler_failure_and_completes_under_a_fresh_registered_executor(
        bool delete
    )
    {
        int id;
        ClaimedJob claim;
        DmsTrustedArtifactPin pin;
        if (delete)
        {
            var seed = await SeedDeleteHandler();
            id = seed.Command.Id;
            claim = seed.Claim;
            pin = seed.Command.PinnedArtifact;
        }
        else
        {
            var seed = await SeedPublication(provider: "mssql");
            id = seed.Command.Id;
            claim = seed.Claim;
            pin = seed.Command.PinnedArtifact;
        }
        var transient = A.Fake<IDmsDataStoreProvisioner>();
        var failure = DmsProvisioningFailure.Create(
            DmsProvisioningFailureCode.LockTimeout,
            DmsProvisioningDiagnostic.ForOperation("ActivationTest")
        );
        A.CallTo(() =>
                transient.CreateAsync(
                    A<DmsDataStoreCreateRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new DmsDataStoreCreateResult.Failure(failure, DmsCleanupReport.Clean));
        A.CallTo(() =>
                transient.DeleteAsync(
                    A<DmsDataStoreDeleteRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new DmsDataStoreDeleteResult.Failure(failure, DmsCleanupReport.Clean));
        using (var host = ActivationHost(true, transient))
        {
            using var client = ManagedClient(host);
            (await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, default))
                .Outcome.Should()
                .Be(JobExecutionOutcome.RetryScheduled);
        }
        await using var connection = await Open();
        var failed = await DeleteRow(connection, id);
        failed["Status"].Should().Be(delete ? "DeleteFailed" : "CreateFailed");
        failed["LastFailureProvisioningCode"].Should().Be("LockTimeout");
        var job = await connection.QuerySingleAsync<(
            string Status,
            DateTime? NextAttemptAt,
            int AttemptCount
        )>(
            """SELECT "Status", "NextAttemptAt", "AttemptCount" FROM dmscs."Job" WHERE "Id" = @Id;""",
            new { claim.Id }
        );
        job.Status.Should().Be("Pending");
        job.NextAttemptAt.Should().NotBeNull();
        job.AttemptCount.Should().Be(claim.AttemptCount);
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "NextAttemptAt" = @Due WHERE "Id" = @Id;""",
            new { claim.Id, Due = DateTime.UtcNow.AddMinutes(-1) }
        );
        var recovered = A.Fake<IDmsDataStoreProvisioner>();
        int admissions = 0;
        A.CallTo(() =>
                recovered.CreateAsync(
                    A<DmsDataStoreCreateRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreCreateRequest _,
                    ManagedDataStoreEndpoint endpoint,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> admit,
                    CancellationToken token
                ) =>
                {
                    await admit(endpoint, token);
                    admissions++;
                    return (DmsDataStoreCreateResult)
                        new DmsDataStoreCreateResult.Reconciled(pin, DmsCleanupReport.Clean);
                }
            );
        A.CallTo(() =>
                recovered.DeleteAsync(
                    A<DmsDataStoreDeleteRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreDeleteRequest _,
                    ManagedDataStoreEndpoint endpoint,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> admit,
                    CancellationToken token
                ) =>
                {
                    await admit(endpoint, token);
                    admissions++;
                    return (DmsDataStoreDeleteResult)new DmsDataStoreDeleteResult.AlreadyAbsent();
                }
            );
        using var restarted = ActivationHost(true, recovered);
        using var restartedClient = ManagedClient(restarted);
        var next = (
            await restarted
                .Services.GetRequiredService<IJobLeaseRepository>()
                .ClaimNext("activation-retry", 300, 5, default)
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        next.JobId.Should().Be(claim.JobId);
        next.AttemptCount.Should().Be(claim.AttemptCount + 1);
        (await restarted.Services.GetRequiredService<JobExecutor>().ExecuteAsync(next, default))
            .Outcome.Should()
            .Be(JobExecutionOutcome.Completed);
        admissions.Should().Be(1);
        (await DeleteRow(connection, id))["Status"].Should().Be(delete ? "Deleted" : "Created");
        (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
            .Should()
            .Be(delete ? 0 : 1);
    }

    [Test, Category("DataStoreManageActivation")]
    public async Task It_uses_production_acceptance_and_reads_and_refuses_new_writes_under_closed_readiness()
    {
        var published = await SeedPublication(name: "published", provider: "mssql");
        await PublicationRepository().PublishCreateAsync(published.Command, published.Execution, default);
        int id;
        using (var host = ActivationHost(true))
        {
            using var client = ManagedClient(host);
            using var accepted = await client.PostAsJsonAsync(
                "/v3/dataStores/manage",
                new { name = "acceptance", databaseTemplate = "Minimal" }
            );
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
            id = int.Parse(
                accepted.Headers.Location!.Segments[^1],
                System.Globalization.CultureInfo.InvariantCulture
            );
            using var get = await client.GetAsync($"/v3/dataStores/manage/{id}");
            get.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var closed = ActivationHost(false);
        using var closedClient = ManagedClient(closed);
        string before = await DeleteAcceptanceSnapshot();
        using var create = await closedClient.PostAsJsonAsync(
            "/v3/dataStores/manage",
            new { name = "refused", databaseTemplate = "Minimal" }
        );
        create.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var delete = await closedClient.DeleteAsync($"/v3/dataStores/manage/{published.Command.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var getClosed = await closedClient.GetAsync($"/v3/dataStores/manage/{id}");
        getClosed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }
}
