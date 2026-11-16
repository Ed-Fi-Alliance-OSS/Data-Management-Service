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
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.ManagedDataStores;

[TestFixture, NonParallelizable, Category("ManagedDataStoreExternalHttp")]
[Explicit("Requires the real managed HTTP provisioning lane; restarts only its owned CMS child.")]
public class Given_external_managed_data_store_readiness
{
    [TestCase(false), TestCase(true)]
    public async Task It_refuses_without_mutation_until_restart_after_catalog_remediation(bool mssql)
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
            await Start(ready: true);
            string clientId = "http-readiness-" + Guid.NewGuid().ToString("N");
            using (
                var register = await client.PostAsync(
                    "connect/register",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["ClientId"] = clientId,
                            ["ClientSecret"] = deployment.ClientSecret,
                            ["DisplayName"] = "External readiness qualification",
                        }
                    )
                )
            )
            {
                register.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            using (
                var tokenResponse = await client.PostAsync(
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
                tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
                string token = tokenJson.RootElement.GetProperty("access_token").GetString()!;
                deployment.RememberSecret(token);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            string prefix = "Http" + Guid.NewGuid().ToString("N")[..12];
            string originalName = prefix + "Ready";
            string refusedName = prefix + "Blocked";
            string refusedTarget = $"EdFi_Ods_{refusedName}_Minimal";
            var (originalLocation, original) = await Create(originalName);
            int ordinaryId = original.GetProperty("dataStoreId").GetInt32();
            string target = original.GetProperty("databaseName").GetString()!;
            await JobsCompleted(1);
            string originalSnapshot = await Snapshot();
            JsonElement ordinary = await GetJson("v3/dataStores/" + ordinaryId);
            string? runtime = deployment.DecryptConnection(
                Convert.FromBase64String(ordinary.GetProperty("connectionString").GetString()!)
            );
            (runtime is not null).Should().BeTrue();
            deployment.RememberSecret(runtime!);
            Guid identity = await PhysicalIdentity();

            // The first host has no active work. Fail the next host's real catalog probe,
            // not a mocked readiness service or a changed administrative server endpoint.
            await deployment.StopAsync();
            provisioning.SetMinimalPackageAvailable(false);
            await Start(ready: false);
            (await Snapshot()).Should().Be(originalSnapshot, "restart must preserve the completed operation");
            await RefuseWithoutMutation();

            // Restoring the actual file while the process lives is insufficient: readiness is latched.
            provisioning.SetMinimalPackageAvailable(true);
            await RefuseWithoutMutation();

            await deployment.StopAsync();
            await Start(ready: true);
            (await Snapshot()).Should().Be(originalSnapshot);
            await Delete(originalLocation);
            (await provisioning.TargetExistsAsync(target)).Should().BeFalse();
            await JobsCompleted(2);

            // Reuse the exact name previously refused. No aggregate, job or reservation was consumed.
            var (retriedLocation, retried) = await Create(refusedName);
            retried.GetProperty("databaseName").GetString().Should().Be(refusedTarget);
            await Delete(retriedLocation);
            await JobsCompleted(4);
            (await provisioning.TargetExistsAsync(refusedTarget)).Should().BeFalse();
            (await GetJson("v3/dataStores")).GetArrayLength().Should().Be(0);
            await using (var connection = await deployment.OpenAsync())
            {
                (
                    await connection.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."DataStoreManage" WHERE "Status" = 'Deleted';"""
                    )
                )
                    .Should()
                    .Be(2);
                (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."Job";"""))
                    .Should()
                    .Be(4);
                (
                    await connection.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "AttemptCount" = 1 AND "LeaseOwner" IS NULL AND "LeaseExpiresAt" IS NULL;"""
                    )
                )
                    .Should()
                    .Be(4);
            }
            provisioning.OperationsAreEmpty.Should().BeTrue();
            deployment.ContainsSecret(deployment.Output).Should().BeFalse();

            async Task Start(bool ready)
            {
                int previousOutputLength = deployment.Output.Length;
                deployment.Start(management: null);
                await deployment.WaitForHttpAsync(client);
                string output = deployment.Output[previousOutputLength..];
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
                    output
                        .Contains("ArtifactMissing", StringComparison.Ordinal)
                        .Should()
                        .BeTrue("the owned package was withheld");
                    output
                        .Contains(
                            "DMS data store provisioning is ready for target provider",
                            StringComparison.Ordinal
                        )
                        .Should()
                        .BeFalse();
                }
            }

            async Task RefuseWithoutMutation()
            {
                using (
                    var refusedCreate = await client.PostAsJsonAsync(
                        "v3/dataStores/manage",
                        new { name = refusedName, databaseTemplate = "Minimal" }
                    )
                )
                {
                    await Unavailable(refusedCreate);
                }
                using (var refusedDelete = await client.DeleteAsync(originalLocation))
                {
                    await Unavailable(refusedDelete);
                }
                (await GetJson(originalLocation.AbsoluteUri)).GetRawText().Should().Be(original.GetRawText());
                (await GetJson("v3/dataStores/" + ordinaryId))
                    .GetRawText()
                    .Should()
                    .Be(ordinary.GetRawText());
                (await Snapshot())
                    .Should()
                    .Be(
                        originalSnapshot,
                        "closed readiness must not alter any aggregate, job, link, pin or timestamp"
                    );
                (await provisioning.TargetExistsAsync(refusedTarget)).Should().BeFalse();
                (await PhysicalIdentity()).Should().Be(identity);
                provisioning.OperationsAreEmpty.Should().BeTrue();
            }

            async Task Unavailable(HttpResponseMessage response)
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                response.Headers.Location.Should().BeNull();
                string body = await response.Content.ReadAsStringAsync();
                body.Contains("Managed data store provisioning is unavailable.", StringComparison.Ordinal)
                    .Should()
                    .BeTrue();
                deployment.ContainsSecret(body).Should().BeFalse();
            }

            async Task<Guid> PhysicalIdentity()
            {
                await using var connection = provisioning.Connection(runtime!);
                await connection.OpenAsync();
                return await connection.ExecuteScalarAsync<Guid>(
                    """SELECT "SourceIdentity" FROM dms."DataStoreIdentity";"""
                );
            }

            async Task<string> Snapshot()
            {
                await using var connection = await deployment.OpenAsync();
                // Hash complete rows so a mismatch never prints internal state or encrypted credentials.
                var aggregates = (
                    await connection.QueryAsync("""SELECT * FROM dmscs."DataStoreManage" ORDER BY "Id";""")
                ).ToArray();
                var jobs = (
                    await connection.QueryAsync("""SELECT * FROM dmscs."Job" ORDER BY "JobId";""")
                ).ToArray();
                var catalog = (
                    await connection.QueryAsync("""SELECT * FROM dmscs."DataStore" ORDER BY "Id";""")
                ).ToArray();
                return Convert.ToHexString(
                    SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new
                            {
                                aggregates,
                                jobs,
                                catalog,
                            }
                        )
                    )
                );
            }

            async Task<(Uri Location, JsonElement Row)> Create(string name)
            {
                using var response = await client.PostAsJsonAsync(
                    "v3/dataStores/manage",
                    new { name, databaseTemplate = "Minimal" }
                );
                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
                Uri location = response.Headers.Location!;
                return (location, await Poll(location, "Created"));
            }

            async Task Delete(Uri location)
            {
                using var response = await client.DeleteAsync(location);
                response.StatusCode.Should().Be(HttpStatusCode.NoContent);
                await Poll(location, "Deleted");
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
                    status
                        .Should()
                        .NotBe("CreateFailed")
                        .And.NotBe("CreateError")
                        .And.NotBe("DeleteFailed")
                        .And.NotBe("DeleteError");
                    if (status == expected)
                    {
                        return row;
                    }
                    await Task.Delay(100, deadline.Token);
                }
            }

            async Task JobsCompleted(int count)
            {
                await using var connection = await deployment.OpenAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (
                    await connection.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."Job" WHERE "Status" = 'Completed';"""
                    ) != count
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
