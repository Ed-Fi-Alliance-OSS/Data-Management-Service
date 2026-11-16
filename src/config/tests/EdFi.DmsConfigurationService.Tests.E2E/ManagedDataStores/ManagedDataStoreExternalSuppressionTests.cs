// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.ManagedDataStores;

[TestFixture, NonParallelizable, Category("ManagedDataStoreExternalHttp")]
[Explicit("Requires the real managed HTTP provisioning lane; restarts an owned CMS with mixed durable work.")]
public class Given_external_managed_data_store_suppression
{
    [TestCase(false), TestCase(true)]
    public async Task It_preserves_managed_work_while_other_job_services_progress_then_resumes_it(bool mssql)
    {
        await using var provisioning = await ManagedDataStoreExternalProvisioning.CreateAsync(mssql);
        await using var deployment = await ManagedDataStoreExternalDeployment.CreateAsync(mssql);
        deployment.ConfigureProvisioning(provisioning.Configuration);
        deployment.RememberSecret(provisioning.Password);
        deployment.RememberSecret(provisioning.RuntimeConnection);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = deployment.BaseAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };
            await Start(management: null, worker: true);
            string clientId = "http-suppression-" + Guid.NewGuid().ToString("N");
            using (
                var response = await client.PostAsync(
                    "connect/register",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["ClientId"] = clientId,
                            ["ClientSecret"] = deployment.ClientSecret,
                            ["DisplayName"] = "External mixed job qualification",
                        }
                    )
                )
            )
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            using (
                var response = await client.PostAsync(
                    "connect/token",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["client_id"] = clientId,
                            ["client_secret"] = deployment.ClientSecret,
                            ["grant_type"] = "client_credentials",
                            ["scope"] = "edfi_admin_api/full_access",
                        }
                    )
                )
            )
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                string token = json.RootElement.GetProperty("access_token").GetString()!;
                deployment.RememberSecret(token);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            string prefix = "Http" + Guid.NewGuid().ToString("N")[..12];
            Uri deleteLocation = await AcceptCreate(prefix + "Delete");
            await Poll(deleteLocation, "Created");
            var original = await ReadRow(deleteLocation);
            await RecordIdentity(original);
            await WaitFor(async () => await JobIsCompleted(original.CreateJobId));

            await deployment.StopAsync();
            await Start(management: null, worker: false);
            Uri createLocation = await AcceptCreate(prefix + "Create");
            using (var response = await client.DeleteAsync(deleteLocation))
            {
                response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            var pendingCreate = await ReadRow(createLocation);
            var pendingDelete = await ReadRow(deleteLocation);
            await RecordIdentity(pendingCreate);
            pendingCreate.Status.Should().Be("PendingCreate");
            pendingCreate.PinnedContractVersion.Should().BeNull();
            pendingDelete.Status.Should().Be("PendingDelete");
            pendingDelete.PinnedPackageSha256.Should().Be(original.PinnedPackageSha256);
            await deployment.StopAsync();

            // There is no public enqueue/schedule API or unrelated production handler. Seed durable
            // fixtures while the process is stopped; the native dispatcher/executor must reject unknown
            // types normally. These are not successful business-handler or HTTP-enqueue assertions.
            string ordinaryId = Guid.NewGuid().ToString("N");
            string nearMatchId = Guid.NewGuid().ToString("N");
            string exhaustedId = Guid.NewGuid().ToString("N");
            string retainedId = Guid.NewGuid().ToString("N");
            long scheduleId;
            await using (var cms = await deployment.OpenAsync())
            {
                DateTime due = await cms.ExecuteScalarAsync<DateTime>(
                    mssql ? "SELECT SYSUTCDATETIME();" : "SELECT clock_timestamp() AT TIME ZONE 'UTC';"
                );
                (
                    await cms.ExecuteScalarAsync<DateTime>(
                        """SELECT MAX("NextAttemptAt") FROM dmscs."Job" WHERE "JobId" IN (@First, @Second);""",
                        new { First = pendingCreate.CreateJobId, Second = pendingDelete.DeleteJobId }
                    )
                )
                    .Should()
                    .BeOnOrBefore(due, "the suppressed managed jobs must lead the due queue");
                foreach (
                    var (id, type, attempts, status) in new[]
                    {
                        (ordinaryId, "Unrelated.Unknown", 0, "Pending"),
                        (nearMatchId, "manageddatastore.create", 0, "Pending"),
                        (exhaustedId, "Unrelated.Exhausted", 5, "Pending"),
                        (retainedId, "Unrelated.Finished", 1, "Completed"),
                    }
                )
                {
                    await cms.ExecuteAsync(
                        """
                        INSERT INTO dmscs."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status",
                            "CreatedAt", "NextAttemptAt", "AttemptCount", "FinishedAt")
                        VALUES (@Id, @Type, 1, '{}', @Status, @Created, @Due, @Attempts, @Finished);
                        """,
                        new
                        {
                            Id = id,
                            Type = type,
                            Status = status,
                            Created = due.AddDays(-8),
                            Due = due,
                            Attempts = attempts,
                            Finished = status == "Completed" ? due.AddDays(-8) : (DateTime?)null,
                        }
                    );
                }
                await cms.ExecuteAsync(
                    """
                    INSERT INTO dmscs."JobSchedule" ("ScheduleType", "JobType", "PayloadVersion", "Payload",
                        "IntervalMinutes", "Enabled", "NextRunAt")
                    VALUES ('External.Unrelated', 'Unrelated.Scheduled', 1, '{}', 1440, @Enabled, @Due);
                    """,
                    new { Enabled = true, Due = due }
                );
                scheduleId = await cms.ExecuteScalarAsync<long>(
                    """SELECT "Id" FROM dmscs."JobSchedule" WHERE "ScheduleType" = 'External.Unrelated';"""
                );
                (
                    await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "JobId" IN (@First, @Second) AND "Status" = 'Pending' AND "AttemptCount" = 0 AND "FencingToken" = 0 AND "LeaseOwner" IS NULL;""",
                        new { First = pendingCreate.CreateJobId, Second = pendingDelete.DeleteJobId }
                    )
                )
                    .Should()
                    .Be(2);
            }
            string paused = await ManagedSnapshot();
            await Start(management: false, worker: true);
            using (
                var response = await client.PostAsJsonAsync(
                    "v3/dataStores/manage",
                    new { name = prefix + "Refused", databaseTemplate = "Minimal" }
                )
            )
            {
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                response.Headers.Location.Should().BeNull();
            }
            using (var response = await client.DeleteAsync(deleteLocation))
            {
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            // Positive progress in every unrelated hosted service bounds the suppression observation.
            await WaitFor(async () =>
            {
                await using var cms = await deployment.OpenAsync();
                return await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "Status" = 'Error';"""
                    ) == 4
                    && await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "JobId" = @Id;""",
                        new { Id = retainedId }
                    ) == 0;
            });
            await using (var cms = await deployment.OpenAsync())
            {
                var errors = (
                    await cms.QueryAsync<(
                        string JobId,
                        string ErrorMessage,
                        int AttemptCount,
                        long FencingToken,
                        string? LeaseOwner,
                        DateTime? LeaseExpiresAt,
                        DateTime? FinishedAt
                    )>(
                        """SELECT "JobId", "ErrorMessage", "AttemptCount", "FencingToken", "LeaseOwner", "LeaseExpiresAt", "FinishedAt" FROM dmscs."Job" WHERE "Status" = 'Error';"""
                    )
                ).ToArray();
                errors.Should().HaveCount(4);
                foreach (var error in errors)
                {
                    error
                        .ErrorMessage.Should()
                        .Be(
                            error.JobId == exhaustedId
                                ? "The job exceeded the maximum number of attempts."
                                : "The job type is not supported by this service."
                        );
                    error.AttemptCount.Should().Be(error.JobId == exhaustedId ? 5 : 1);
                    if (error.JobId != exhaustedId)
                    {
                        error.FencingToken.Should().BeGreaterThan(0);
                    }
                    error.LeaseOwner.Should().BeNull();
                    error.LeaseExpiresAt.Should().BeNull();
                    error.FinishedAt.Should().NotBeNull();
                }
                var schedule = await cms.QuerySingleAsync<(
                    DateTime NextRunAt,
                    DateTime LastEnqueuedOccurrence,
                    long FencingToken,
                    string? LeaseOwner,
                    DateTime? LeaseExpiresAt
                )>(
                    """SELECT "NextRunAt", "LastEnqueuedOccurrence", "FencingToken", "LeaseOwner", "LeaseExpiresAt" FROM dmscs."JobSchedule" WHERE "Id" = @Id;""",
                    new { Id = scheduleId }
                );
                // Read back the provider-stored occurrence rather than assume datetime precision.
                var occurrence = await cms.QuerySingleAsync<(string JobId, DateTime ScheduledOccurrence)>(
                    """SELECT "JobId", "ScheduledOccurrence" FROM dmscs."Job" WHERE "SourceScheduleId" = @Id;""",
                    new { Id = scheduleId }
                );
                errors
                    .Select(error => error.JobId)
                    .Should()
                    .BeEquivalentTo(ordinaryId, nearMatchId, exhaustedId, occurrence.JobId);
                schedule.LastEnqueuedOccurrence.Should().Be(occurrence.ScheduledOccurrence);
                schedule.NextRunAt.Should().BeAfter(schedule.LastEnqueuedOccurrence);
                schedule.NextRunAt.Should().BeAfter(DateTime.UtcNow);
                schedule.FencingToken.Should().BeGreaterThan(0);
                schedule.LeaseOwner.Should().BeNull();
                schedule.LeaseExpiresAt.Should().BeNull();
            }
            (await ManagedSnapshot())
                .Should()
                .Be(
                    paused,
                    "disabled management must preserve every aggregate, job counter/fence and catalog column"
                );
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();
            (await provisioning.TargetExistsAsync(pendingDelete.TargetDatabaseName)).Should().BeTrue();
            provisioning.OperationsAreEmpty.Should().BeTrue();
            await deployment.StopAsync();
            (await ManagedSnapshot()).Should().Be(paused);

            await Start(management: true, worker: true);
            await Poll(createLocation, "Created");
            await Poll(deleteLocation, "Deleted");
            await WaitFor(async () =>
                await JobIsCompleted(pendingCreate.CreateJobId)
                && await JobIsCompleted(pendingDelete.DeleteJobId!)
            );
            var resumedCreate = await ReadRow(createLocation);
            var resumedDelete = await ReadRow(deleteLocation);
            foreach (
                var (accepted, resumed) in new[]
                {
                    (pendingCreate, resumedCreate),
                    (pendingDelete, resumedDelete),
                }
            )
            {
                resumed.Id.Should().Be(accepted.Id);
                resumed.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
                resumed.EndpointDescriptor.Should().Be(accepted.EndpointDescriptor);
                resumed.TargetDatabaseName.Should().Be(accepted.TargetDatabaseName);
                resumed.CreateJobId.Should().Be(accepted.CreateJobId);
                resumed.DeleteJobId.Should().Be(accepted.DeleteJobId);
                resumed.LastAttempt.Should().Be(1);
                resumed.LastFailureCategory.Should().BeNull();
            }
            resumedCreate.DataStoreId.Should().NotBeNull();
            resumedCreate.PinnedPackageSha256.Should().NotBeNullOrEmpty();
            resumedDelete.DataStoreId.Should().BeNull();
            resumedDelete.PinnedPackageSha256.Should().Be(pendingDelete.PinnedPackageSha256);
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeTrue();
            (await provisioning.TargetExistsAsync(pendingDelete.TargetDatabaseName)).Should().BeFalse();
            await using (var cms = await deployment.OpenAsync())
            {
                (await cms.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(1);
                (
                    await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "JobId" IN (@First, @Second) AND "Status" = 'Completed' AND "AttemptCount" = 1 AND "FencingToken" > 0 AND "LeaseOwner" IS NULL AND "LeaseExpiresAt" IS NULL;""",
                        new { First = pendingCreate.CreateJobId, Second = pendingDelete.DeleteJobId }
                    )
                )
                    .Should()
                    .Be(2);
            }
            using (var response = await client.DeleteAsync(createLocation))
            {
                response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            await Poll(createLocation, "Deleted");
            await WaitFor(async () => await JobIsCompleted((await ReadRow(createLocation)).DeleteJobId!));
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();
            provisioning.OperationsAreEmpty.Should().BeTrue();
            deployment.ContainsSecret(deployment.Output).Should().BeFalse();

            async Task Start(bool? management, bool worker)
            {
                provisioning.Configuration["JobSettings__WorkerEnabled"] = worker ? "true" : "false";
                int offset = deployment.Output.Length;
                deployment.Start(management);
                await deployment.WaitForHttpAsync(client);
                deployment
                    .Output[offset..]
                    .Contains(
                        "DMS data store provisioning is ready for target provider",
                        StringComparison.Ordinal
                    )
                    .Should()
                    .BeTrue();
            }
            async Task RecordIdentity(ManagedDataStoreRow row) =>
                await TestContext.Progress.WriteLineAsync(
                    $"Owned target: {row.TargetDatabaseName}; expected source identity: {row.ExpectedSourceIdentity:D}."
                );
            async Task<ManagedDataStoreRow> ReadRow(Uri location)
            {
                await using var cms = await deployment.OpenAsync();
                return await cms.QuerySingleAsync<ManagedDataStoreRow>(
                    """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                    new { Id = int.Parse(location.Segments[^1]) }
                );
            }
            async Task<string> ManagedSnapshot()
            {
                await using var cms = await deployment.OpenAsync();
                var rows = (
                    await cms.QueryAsync("""SELECT * FROM dmscs."DataStoreManage" ORDER BY "Id";""")
                ).ToArray();
                var jobs = (
                    await cms.QueryAsync(
                        """SELECT * FROM dmscs."Job" WHERE "JobId" IN (@Create, @Delete, @Original) ORDER BY "Id";""",
                        new
                        {
                            Create = pendingCreate.CreateJobId,
                            Delete = pendingDelete.DeleteJobId,
                            Original = original.CreateJobId,
                        }
                    )
                ).ToArray();
                var catalog = (
                    await cms.QueryAsync("""SELECT * FROM dmscs."DataStore" ORDER BY "Id";""")
                ).ToArray();
                return Convert.ToHexString(
                    SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new
                            {
                                rows,
                                jobs,
                                catalog,
                            }
                        )
                    )
                );
            }
            async Task<Uri> AcceptCreate(string name)
            {
                using var response = await client.PostAsJsonAsync(
                    "v3/dataStores/manage",
                    new { name, databaseTemplate = "Minimal" }
                );
                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
                return response.Headers.Location!;
            }
            async Task Poll(Uri location, string expected) =>
                await WaitFor(async () =>
                {
                    using var response = await client.GetAsync(location);
                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                    string body = await response.Content.ReadAsStringAsync();
                    deployment.ContainsSecret(body).Should().BeFalse();
                    using var json = JsonDocument.Parse(body);
                    return json.RootElement.GetProperty("status").GetString() == expected;
                });
            async Task<bool> JobIsCompleted(string id)
            {
                await using var cms = await deployment.OpenAsync();
                return await cms.ExecuteScalarAsync<string>(
                        """SELECT "Status" FROM dmscs."Job" WHERE "JobId" = @Id;""",
                        new { Id = id }
                    ) == "Completed";
            }
            async Task WaitFor(Func<Task<bool>> condition)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (!await condition())
                {
                    await Task.Delay(100, deadline.Token);
                }
            }
        }
        catch
        {
            await TestContext.Progress.WriteLineAsync(deployment.RedactedOutput());
            throw;
        }
    }
}
