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
public class Given_external_managed_data_store_terminal_readiness
{
    [TestCase(false), TestCase(true)]
    public async Task It_terminalizes_previously_accepted_create_and_delete_without_revival(bool mssql)
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
            string clientId = "http-terminal-" + Guid.NewGuid().ToString("N");
            using (
                var register = await client.PostAsync(
                    "connect/register",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["ClientId"] = clientId,
                            ["ClientSecret"] = deployment.ClientSecret,
                            ["DisplayName"] = "External terminal readiness qualification",
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
            Uri deleteLocation = await AcceptCreate(prefix + "Retained");
            JsonElement created = await Poll(deleteLocation, "Created");
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
            Uri createLocation = await AcceptCreate(prefix + "Pending");
            using (var response = await client.DeleteAsync(deleteLocation))
            {
                response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            var pendingCreate = await ReadRow(createLocation);
            var pendingDelete = await ReadRow(deleteLocation);
            pendingCreate.Status.Should().Be("PendingCreate");
            pendingCreate.PinnedContractVersion.Should().BeNull();
            pendingDelete.Status.Should().Be("PendingDelete");
            pendingDelete.PinnedPackageSha256.Should().Be(original.PinnedPackageSha256);
            string[] jobIds = [pendingCreate.CreateJobId, pendingDelete.DeleteJobId!];
            int[] ids = [pendingCreate.Id, pendingDelete.Id];
            Dictionary<string, DateTime> acceptedDueTimes;
            await using (var cms = await deployment.OpenAsync())
            {
                acceptedDueTimes = (
                    await cms.QueryAsync<(string JobId, DateTime NextAttemptAt)>(
                        """SELECT "JobId", "NextAttemptAt" FROM dmscs."Job" WHERE "JobId" IN (@First, @Second);""",
                        new { First = jobIds[0], Second = jobIds[1] }
                    )
                ).ToDictionary(row => row.JobId, row => row.NextAttemptAt);
                (
                    await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "JobId" IN (@First, @Second) AND "Status" = 'Pending' AND "AttemptCount" = 0 AND "FencingToken" = 0 AND "LeaseOwner" IS NULL;""",
                        new { First = jobIds[0], Second = jobIds[1] }
                    )
                )
                    .Should()
                    .Be(2);
            }
            string pendingSnapshot = await Snapshot();
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();
            await VerifyRetainedTarget();

            await deployment.StopAsync();
            (await Snapshot())
                .Should()
                .Be(pendingSnapshot, "both accepted jobs must still be unclaimed at shutdown");
            provisioning.SetMinimalPackageAvailable(false);
            await Start(ready: false, worker: true);
            JsonElement createError = await Poll(createLocation, "CreateError");
            JsonElement deleteError = await Poll(deleteLocation, "DeleteError");
            foreach (
                var (location, accepted, jobId) in new[]
                {
                    (createLocation, pendingCreate, pendingCreate.CreateJobId),
                    (deleteLocation, pendingDelete, pendingDelete.DeleteJobId!),
                }
            )
            {
                await JobStatus(jobId, "Error");
                var failed = await ReadRow(location);
                failed.LastAttempt.Should().Be(1);
                failed.LastFailureCategory.Should().Be("NotReady");
                failed.LastFailureProvisioningCode.Should().Be("StartupValidationFailed");
                failed.LastFailureJobCode.Should().BeNull();
                failed
                    .Should()
                    .BeEquivalentTo(
                        accepted,
                        options =>
                            options
                                .Excluding(row => row.Status)
                                .Excluding(row => row.LastAttempt)
                                .Excluding(row => row.LastFailureCategory)
                                .Excluding(row => row.LastFailureProvisioningCode)
                                .Excluding(row => row.UpdatedAt)
                                .Excluding(row => row.LastModifiedDate)
                                .Excluding(row => row.LastRefreshed)
                    );
                await using var cms = await deployment.OpenAsync();
                var job = await cms.QuerySingleAsync<(
                    int AttemptCount,
                    long FencingToken,
                    string? LeaseOwner,
                    DateTime? LeaseExpiresAt,
                    DateTime? NextAttemptAt,
                    DateTime FinishedAt,
                    DateTime LastModifiedAt,
                    string ErrorMessage
                )>(
                    """SELECT "AttemptCount", "FencingToken", "LeaseOwner", "LeaseExpiresAt", "NextAttemptAt", "FinishedAt", "LastModifiedAt", "ErrorMessage" FROM dmscs."Job" WHERE "JobId" = @JobId;""",
                    new { JobId = jobId }
                );
                job.AttemptCount.Should().Be(1);
                job.FencingToken.Should().BeGreaterThan(0);
                job.LeaseOwner.Should().BeNull();
                job.LeaseExpiresAt.Should().BeNull();
                job.NextAttemptAt.Should()
                    .Be(acceptedDueTimes[jobId], "terminal failure must not schedule a retry");
                job.ErrorMessage.Should().Be("The job handler failed.");
                job.FinishedAt.Should().Be(failed.UpdatedAt);
                job.LastModifiedAt.Should().Be(failed.LastModifiedDate);
                failed.LastRefreshed.Should().Be(failed.LastModifiedDate);
            }
            foreach (string field in new[] { "dataStoreId", "dataStoreName", "databaseName" })
            {
                createError.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
                deleteError
                    .GetProperty(field)
                    .GetRawText()
                    .Should()
                    .Be(created.GetProperty(field).GetRawText());
            }
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();
            await VerifyRetainedTarget();
            provisioning.OperationsAreEmpty.Should().BeTrue();
            string terminalSnapshot = await Snapshot();

            await deployment.StopAsync();
            provisioning.SetMinimalPackageAvailable(true);
            await Start(ready: true, worker: true);
            (await Snapshot()).Should().Be(terminalSnapshot);
            // Completing newer work is a real worker-progress observation, not a sleep asserting no revival.
            Uri control = await AcceptCreate(prefix + "Control");
            JsonElement controlCreated = await Poll(control, "Created");
            using (var response = await client.DeleteAsync(control))
            {
                response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            await Poll(control, "Deleted");
            var controlRow = await ReadRow(control);
            await JobStatus(controlRow.DeleteJobId!, "Completed");
            (await Snapshot())
                .Should()
                .Be(terminalSnapshot, "Ready must not revive settled Error jobs or reset lifecycle failures");
            foreach (
                var (location, name, expected) in new[]
                {
                    (createLocation, pendingCreate.Name, "CreateError"),
                    (deleteLocation, pendingDelete.Name, "DeleteError"),
                }
            )
            {
                (await GetJson(location.AbsoluteUri)).GetProperty("status").GetString().Should().Be(expected);
                using var duplicate = await client.PostAsJsonAsync(
                    "v3/dataStores/manage",
                    new { name, databaseTemplate = "Minimal" }
                );
                duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                using var deletion = await client.DeleteAsync(location);
                deletion.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            (await Snapshot()).Should().Be(terminalSnapshot);
            await VerifyRetainedTarget();
            (await provisioning.TargetExistsAsync(pendingCreate.TargetDatabaseName)).Should().BeFalse();
            (await provisioning.TargetExistsAsync(controlCreated.GetProperty("databaseName").GetString()!))
                .Should()
                .BeFalse();
            await using (var cms = await deployment.OpenAsync())
            {
                (await cms.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."Job";""")).Should().Be(5);
                (
                    await cms.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "Status" = 'Error';"""
                    )
                )
                    .Should()
                    .Be(2);
                (await cms.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(1);
            }
            deployment.ContainsSecret(deployment.Output).Should().BeFalse();
            provisioning.OperationsAreEmpty.Should().BeTrue();

            // Post-assertion cleanup only: DeleteError correctly retains this target and cannot be retried
            // through the API. Stop the worker and use the real endpoint/identity-checked physical protocol.
            await deployment.StopAsync();
            await provisioning.RemoveVerifiedTargetAsync(
                original.TargetDatabaseName,
                original.ExpectedSourceIdentity,
                original.EndpointDescriptor
            );
            (await provisioning.TargetExistsAsync(original.TargetDatabaseName)).Should().BeFalse();
            (await Snapshot())
                .Should()
                .Be(terminalSnapshot, "cleanup must not reset or repair the CMS aggregate/job");

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
                return Convert.ToHexString(
                    SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { rows, jobs }))
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
                    if (row.GetProperty("status").GetString() == expected)
                    {
                        return row;
                    }
                    await Task.Delay(100, deadline.Token);
                }
            }
            async Task JobStatus(string id, string expected)
            {
                await using var cms = await deployment.OpenAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
