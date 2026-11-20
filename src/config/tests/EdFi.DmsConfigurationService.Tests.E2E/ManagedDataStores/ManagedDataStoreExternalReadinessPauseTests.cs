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
[Explicit("Requires the real managed HTTP provisioning lane; queues work before a closed-readiness restart.")]
public class Given_external_managed_data_store_readiness_pause
{
    [TestCase(false), TestCase(true)]
    public async Task It_pauses_previously_accepted_create_and_delete_then_resumes_them_when_ready(bool mssql)
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
            await Start(ready: true, worker: true);
            string clientId = "http-pause-" + Guid.NewGuid().ToString("N");
            using (
                var register = await client.PostAsync(
                    "connect/register",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["ClientId"] = clientId,
                            ["ClientSecret"] = deployment.ClientSecret,
                            ["DisplayName"] = "External readiness pause qualification",
                        }
                    )
                )
            )
            {
                register.StatusCode.Should().Be(HttpStatusCode.OK);
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
            (Uri deleteLocation, _) = await AcceptCreate(prefix + "Retained");
            await Poll(deleteLocation, "Created");
            var original = await ReadRow(deleteLocation);
            // Preserve independent CMS evidence for operator inspection if a later assertion fails.
            await TestContext.Progress.WriteLineAsync(
                $"Owned target: {original.TargetDatabaseName}; expected source identity: {original.ExpectedSourceIdentity:D}."
            );
            await JobStatus(original.CreateJobId, "Completed");
            JsonElement ordinary = await GetJson("v3/dataStores/" + original.DataStoreId);
            string? runtime = deployment.DecryptConnection(
                Convert.FromBase64String(ordinary.GetProperty("connectionString").GetString()!)
            );
            (runtime is not null).Should().BeTrue();
            deployment.RememberSecret(runtime!);

            await deployment.StopAsync();
            await Start(ready: true, worker: false);
            (Uri createLocation, string createJobId) = await AcceptCreate(prefix + "Pending");
            string deleteJobId;
            using (var response = await client.DeleteAsync(deleteLocation))
            {
                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
                deleteJobId = await QueuedJobId(response, "The Data Store has been queued to be deleted.");
            }
            var pendingCreate = await ReadRow(createLocation);
            var pendingDelete = await ReadRow(deleteLocation);
            pendingCreate.Status.Should().Be("PendingCreate");
            pendingCreate.CreateJobId.Should().Be(createJobId);
            pendingDelete.Status.Should().Be("PendingDelete");
            pendingDelete.DeleteJobId.Should().Be(deleteJobId);
            string[] jobIds = [createJobId, deleteJobId];
            int[] ids = [pendingCreate.Id, pendingDelete.Id];
            await deployment.StopAsync();

            // There is no unrelated production handler or public enqueue API: an unknown type settled by the
            // production executor is the observable worker progress. The managed jobs lead the due queue.
            string unrelatedId = Guid.NewGuid().ToString("N");
            await using (var cms = await deployment.OpenAsync())
            {
                DateTime due = await cms.ExecuteScalarAsync<DateTime>(
                    mssql ? "SELECT SYSUTCDATETIME();" : "SELECT clock_timestamp() AT TIME ZONE 'UTC';"
                );
                (
                    await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "JobId" IN (@First, @Second) AND "Status" = 'Pending' AND "AttemptCount" = 0 AND "FencingToken" = 0 AND "LeaseOwner" IS NULL AND "NextAttemptAt" <= @Due;""",
                        new
                        {
                            First = jobIds[0],
                            Second = jobIds[1],
                            Due = due,
                        }
                    )
                ).Should().Be(2);
                await cms.ExecuteAsync(
                    """
                    INSERT INTO dmscs."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status",
                        "CreatedAt", "NextAttemptAt", "AttemptCount")
                    VALUES (@Id, 'Unrelated.Unknown', 1, '{}', 'Pending', @Due, @Due, 0);
                    """,
                    new { Id = unrelatedId, Due = due }
                );
            }
            string paused = await Snapshot();
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();

            provisioning.SetMinimalPackageAvailable(false);
            await Start(ready: false, worker: true);
            // Positive worker progress bounds the observation: the unrelated job queued behind both managed
            // jobs is settled while neither managed job is claimed, attempted or exhausted.
            await JobStatus(unrelatedId, "Error");
            (await Snapshot())
                .Should()
                .Be(paused, "closed readiness must leave accepted managed work untouched");
            (await GetJson(createLocation.AbsoluteUri))
                .GetProperty("status")
                .GetString()
                .Should()
                .Be("PendingCreate");
            (await GetJson(deleteLocation.AbsoluteUri))
                .GetProperty("status")
                .GetString()
                .Should()
                .Be("PendingDelete");
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();
            await VerifyRetainedTarget();
            provisioning.OperationsAreEmpty.Should().BeTrue();
            await deployment.StopAsync();
            (await Snapshot()).Should().Be(paused);

            provisioning.SetMinimalPackageAvailable(true);
            await Start(ready: true, worker: true);
            JsonElement created = await Poll(createLocation, "Created");
            await Poll(deleteLocation, "Deleted");
            await JobStatus(createJobId, "Completed");
            await JobStatus(deleteJobId, "Completed");
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
            resumedDelete.PinnedPackageSha256.Should().Be(pendingDelete.PinnedPackageSha256);
            resumedDelete.DataStoreId.Should().BeNull();
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeTrue();
            (await provisioning.TargetExistsAsync(original.TargetDatabaseName)).Should().BeFalse();
            // The deleted store's routing row is gone; the created store has exactly one registration.
            using (var stale = await client.GetAsync("v3/dataStores/" + original.DataStoreId))
            {
                stale.StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            created.GetProperty("dataStoreId").GetInt32().Should().Be(resumedCreate.DataStoreId!.Value);
            await using (var cms = await deployment.OpenAsync())
            {
                (await cms.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(1);
                (
                    await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "JobId" IN (@First, @Second) AND "Status" = 'Completed' AND "AttemptCount" = 1 AND "FencingToken" > 0 AND "LeaseOwner" IS NULL AND "LeaseExpiresAt" IS NULL;""",
                        new { First = jobIds[0], Second = jobIds[1] }
                    )
                )
                    .Should()
                    .Be(2);
                (
                    await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "Status" = 'Error';"""
                    )
                )
                    .Should()
                    .Be(1, "only the unrelated unknown-type job ends in Error");
            }

            // Remove the remaining target through the qualified API before fixture disposal.
            using (var response = await client.DeleteAsync(createLocation))
            {
                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            }
            await Poll(createLocation, "Deleted");
            await JobStatus((await ReadRow(createLocation)).DeleteJobId!, "Completed");
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();
            provisioning.OperationsAreEmpty.Should().BeTrue();
            deployment.ContainsSecret(deployment.Output).Should().BeFalse();

            async Task Start(bool ready, bool worker)
            {
                provisioning.Configuration["JobSettings__WorkerEnabled"] = worker ? "true" : "false";
                int offset = deployment.Output.Length;
                deployment.Start(management: null);
                await deployment.WaitForHttpAsync(client);
                string output = deployment.Output[offset..];
                output
                    .Contains(
                        ready
                            ? "DMS data store provisioning is ready for target provider"
                            : "DMS data store provisioning is not ready for target provider",
                        StringComparison.Ordinal
                    )
                    .Should()
                    .BeTrue();
                if (!ready)
                {
                    output.Contains("ArtifactMissing", StringComparison.Ordinal).Should().BeTrue();
                }
            }
            async Task VerifyRetainedTarget()
            {
                (await GetJson("v3/dataStores/" + original.DataStoreId))
                    .GetRawText()
                    .Should()
                    .Be(ordinary.GetRawText());
                await using var physical = provisioning.Connection(runtime!);
                await physical.OpenAsync();
                (
                    await physical.ExecuteScalarAsync<Guid>(
                        """SELECT "SourceIdentity" FROM dms."DataStoreIdentity";"""
                    )
                )
                    .Should()
                    .Be(original.ExpectedSourceIdentity);
            }
            async Task<ManagedDataStoreRow> ReadRow(Uri location)
            {
                await using var cms = await deployment.OpenAsync();
                return await cms.QuerySingleAsync<ManagedDataStoreRow>(
                    """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                    new { Id = int.Parse(location.Segments[^1]) }
                );
            }
            async Task<string> Snapshot()
            {
                await using var cms = await deployment.OpenAsync();
                var rows = (
                    await cms.QueryAsync(
                        """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" IN (@First, @Second) ORDER BY "Id";""",
                        new { First = ids[0], Second = ids[1] }
                    )
                ).ToArray();
                var jobs = (
                    await cms.QueryAsync(
                        """SELECT * FROM dmscs."Job" WHERE "JobId" IN (@First, @Second) ORDER BY "JobId";""",
                        new { First = jobIds[0], Second = jobIds[1] }
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
            async Task<(Uri Location, string JobId)> AcceptCreate(string name)
            {
                using var response = await client.PostAsJsonAsync(
                    "v3/dataStores/manage",
                    new { name, databaseTemplate = "Minimal" }
                );
                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
                return (
                    response.Headers.Location!,
                    await QueuedJobId(response, "The Data Store has been queued to be created.")
                );
            }
            static async Task<string> QueuedJobId(HttpResponseMessage response, string message)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                json.RootElement.EnumerateObject()
                    .Select(property => property.Name)
                    .Should()
                    .Equal("jobId", "message");
                json.RootElement.GetProperty("message").GetString().Should().Be(message);
                return json.RootElement.GetProperty("jobId").GetString()!;
            }
            async Task<JsonElement> GetJson(string path)
            {
                using var response = await client.GetAsync(path);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                string body = await response.Content.ReadAsStringAsync();
                deployment.ContainsSecret(body).Should().BeFalse();
                using var json = JsonDocument.Parse(body);
                return json.RootElement.Clone();
            }
            async Task<JsonElement> Poll(Uri location, string expected)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (true)
                {
                    JsonElement row = await GetJson(location.AbsoluteUri);
                    string status = row.GetProperty("status").GetString()!;
                    new[] { "CreateFailed", "CreateError", "DeleteFailed", "DeleteError" }
                        .Should()
                        .NotContain(status);
                    if (status == expected)
                    {
                        return row;
                    }
                    await Task.Delay(100, deadline.Token);
                }
            }
            async Task JobStatus(string id, string expected)
            {
                await using var cms = await deployment.OpenAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (
                    await cms.ExecuteScalarAsync<string>(
                        """SELECT "Status" FROM dmscs."Job" WHERE "JobId" = @Id;""",
                        new { Id = id }
                    ) != expected
                )
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
