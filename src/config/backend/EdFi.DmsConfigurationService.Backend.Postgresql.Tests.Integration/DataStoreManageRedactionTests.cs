// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    [Test, Category("DataStoreManageRedaction")]
    public async Task It_excludes_persisted_identity_and_secret_canaries_from_managed_surfaces(
        [Values(false, true)] bool delete,
        [Values(false, true)] bool fail
    )
    {
        int id;
        Guid identity;
        ClaimedJob claim;
        DmsTrustedArtifactPin pin;
        if (delete)
        {
            var seed = await SeedDeleteHandler();
            id = seed.Command.Id;
            identity = seed.Command.Identity.ExpectedSourceIdentity;
            claim = seed.Claim;
            pin = seed.Command.PinnedArtifact;
        }
        else
        {
            var seed = await SeedPublication(provider: "postgresql");
            id = seed.Command.Id;
            identity = seed.Command.Identity.ExpectedSourceIdentity;
            claim = seed.Claim;
            pin = seed.Command.PinnedArtifact;
        }
        await using var connection = await Open();
        (
            await connection.ExecuteScalarAsync<Guid>(
                """SELECT "ExpectedSourceIdentity" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = id }
            ) == identity
        )
            .Should()
            .BeTrue("the identity canary must be the actual persisted identity");

        string runtimePassword = "G6Runtime" + Guid.NewGuid().ToString("N");
        string adminPassword = "G6Admin" + Guid.NewGuid().ToString("N");
        string packagePassword = "G6Package" + Guid.NewGuid().ToString("N");
        string runtime = "Host=db.example;Username=runtime;Password=" + runtimePassword;
        string admin = "Host=db.example;Username=admin;Password=" + adminPassword;
        string artifact = "https://package-user:" + packagePassword + "@packages.invalid/template.nupkg";
        string[] canaries =
        [
            identity.ToString("D"),
            identity.ToString("N"),
            identity.ToString("B"),
            identity.ToString("P"),
            runtimePassword,
            adminPassword,
            packagePassword,
            runtime,
            admin,
            artifact,
        ];
        // Exercise the real diagnostic projection on a hostile provider/artifact exception before
        // the simulated provisioner returns a typed failure. No network or physical engine runs here.
        var cause = new IOException(string.Join(" ", canaries));
        cause.Data["artifact"] = artifact;
        var failure = DmsProvisioningFailure.Create(
            DmsProvisioningFailureCode.TargetUnowned,
            DmsProvisioningDiagnostic.From(cause, "InspectTarget")
        );
        AssertRedacted(JsonSerializer.Serialize(failure));
        var provisioner = A.Fake<IDmsDataStoreProvisioner>();
        int admissions = 0;
        A.CallTo(() =>
                provisioner.CreateAsync(
                    A<DmsDataStoreCreateRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreCreateRequest request,
                    ManagedDataStoreEndpoint endpoint,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> admit,
                    CancellationToken token
                ) =>
                {
                    (request.ExpectedSourceIdentity == identity).Should().BeTrue();
                    await admit(endpoint, token);
                    admissions++;
                    return fail
                        ? (DmsDataStoreCreateResult)
                            new DmsDataStoreCreateResult.Failure(failure, DmsCleanupReport.Clean)
                        : new DmsDataStoreCreateResult.Reconciled(pin, DmsCleanupReport.Clean);
                }
            );
        A.CallTo(() =>
                provisioner.DeleteAsync(
                    A<DmsDataStoreDeleteRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreDeleteRequest request,
                    ManagedDataStoreEndpoint endpoint,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> admit,
                    CancellationToken token
                ) =>
                {
                    (request.ExpectedSourceIdentity == identity).Should().BeTrue();
                    await admit(endpoint, token);
                    admissions++;
                    return fail
                        ? (DmsDataStoreDeleteResult)
                            new DmsDataStoreDeleteResult.Failure(failure, DmsCleanupReport.Clean)
                        : new DmsDataStoreDeleteResult.AlreadyAbsent();
                }
            );
        using var logs = new CapturingLoggerProvider();
        using var host = DmsDataStoreProvisioningStartupTests.Given_managed_data_store_startup.Host(
            "true",
            true,
            target: "postgresql",
            catalog: "postgresql",
            overrides: new()
            {
                ["DatabaseSettings:DatabaseConnection"] = _database.ConnectionString,
                ["DatabaseSettings:EncryptionKey"] = PublicationOptions.Value.EncryptionKey.PadRight(32, '0'),
                ["DmsDataStoreProvisioning:AdminConnectionString"] = admin,
                ["DataStoreManagement:RuntimeConnectionString"] = runtime,
            },
            configure: services =>
            {
                services.AddSingleton(provisioner);
                services.AddSingleton<ILoggerProvider>(logs);
            }
        );
        using var client = ManagedClient(host);
        (await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, default))
            .Outcome.Should()
            .Be(fail ? JobExecutionOutcome.Failed : JobExecutionOutcome.Completed);
        admissions.Should().Be(1, "the actual persisted identity must reach guarded admission");
        var row = await DeleteRow(connection, id);
        string expectedState = (delete, fail) switch
        {
            (true, true) => "DeleteError",
            (true, false) => "Deleted",
            (false, true) => "CreateError",
            _ => "Created",
        };
        row["Status"].Should().Be(expectedState);
        if (fail)
        {
            row["LastFailureProvisioningCode"].Should().Be("TargetUnowned");
        }
        if (!delete && !fail)
        {
            byte[] encrypted = await connection.QuerySingleAsync<byte[]>(
                """SELECT "ConnectionString" FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                new { Id = row["DataStoreId"] }
            );
            using var scope = host.Services.CreateScope();
            string? plaintext = scope
                .ServiceProvider.GetRequiredService<IConnectionStringEncryptionService>()
                .Decrypt(encrypted);
            (plaintext?.Contains(runtimePassword, StringComparison.Ordinal) == true)
                .Should()
                .BeTrue("the published runtime credential canary must actually be used");
        }
        foreach (
            string path in new[]
            {
                "/v3/dataStores/manage",
                $"/v3/dataStores/manage/{id}",
                $"/v3/jobs/{claim.JobId}",
            }
        )
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            string body = await response.Content.ReadAsStringAsync();
            AssertRedacted(body);
            using var json = JsonDocument.Parse(body);
            if (path.StartsWith("/v3/jobs/", StringComparison.Ordinal))
            {
                json.RootElement.GetProperty("status").GetString().Should().Be(fail ? "Error" : "Completed");
                json.RootElement.GetProperty("errorMessage")
                    .GetString()
                    .Should()
                    .Be(fail ? "The job handler failed." : null);
            }
        }
        var storedJob = await connection.QuerySingleAsync<(string Payload, string? ErrorMessage)>(
            """SELECT "Payload", "ErrorMessage" FROM dmscs."Job" WHERE "Id" = @Id;""",
            new { claim.Id }
        );
        AssertRedacted(storedJob.Payload);
        AssertRedacted(storedJob.ErrorMessage ?? "");
        (
            logs.Entries.Any(entry =>
                entry.EventId.Name == (fail ? "JobFailed" : "JobCompleted")
                && entry.Message.Contains(claim.JobId, StringComparison.Ordinal)
            )
        )
            .Should()
            .BeTrue("captured production settlement logs must make the redaction assertion non-vacuous");
        foreach (var entry in logs.Entries)
        {
            AssertRedacted(entry.Message);
            foreach (var field in entry.State)
            {
                AssertRedacted(field.Key);
                AssertRedacted(field.Value?.ToString() ?? "");
            }
            AssertRedacted(entry.Exception?.ToString() ?? "");
        }

        void AssertRedacted(string text) =>
            Array
                .Exists(canaries, canary => text.Contains(canary, StringComparison.OrdinalIgnoreCase))
                .Should()
                .BeFalse("managed responses, job data and application logs must exclude internal canaries");
    }
}
