// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.ManagedDataStores;

[TestFixture, NonParallelizable, Category("ManagedDataStoreExternalHttp")]
[Explicit(
    "Requires genuine packages, provider admin connections, psql/shared SQL staging and a fresh production CMS build."
)]
public class Given_external_managed_data_store_lifecycle
{
    [TestCase(false), TestCase(true)]
    public async Task It_provisions_and_deletes_through_external_http_and_the_real_worker(bool mssql)
    {
        await using var provisioning = await ManagedDataStoreExternalProvisioning.CreateAsync(mssql);
        // Reverse disposal order stops the owned child before releasing its provisioning resources.
        await using var deployment = await ManagedDataStoreExternalDeployment.CreateAsync(mssql);
        deployment.ConfigureProvisioning(provisioning.Configuration);
        deployment.RememberSecret(provisioning.Password);
        deployment.RememberSecret(provisioning.RuntimeConnection);
        try
        {
            deployment.Start(management: null); // Exercise the shipped default true with valid configuration.
            using var client = new HttpClient
            {
                BaseAddress = deployment.BaseAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };
            await deployment.WaitForHttpAsync(client);
            deployment
                .Output.Contains(
                    "DMS data store provisioning is ready for target provider",
                    StringComparison.Ordinal
                )
                .Should()
                .BeTrue();
            string clientId = "http-lifecycle-" + Guid.NewGuid().ToString("N");
            using (
                var register = await client.PostAsync(
                    "connect/register",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["ClientId"] = clientId,
                            ["ClientSecret"] = deployment.ClientSecret,
                            ["DisplayName"] = "External lifecycle qualification",
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
            string ordinaryName = prefix + "café";
            string managedName = prefix + "cafe";
            string routingProvider = mssql ? "sqlserver" : "postgresql";
            using var ordinaryInsert = await client.PostAsJsonAsync(
                "v3/dataStores",
                new
                {
                    name = ordinaryName,
                    dataStoreType = "SharedInstance",
                    provider = routingProvider,
                }
            );
            ordinaryInsert.StatusCode.Should().Be(HttpStatusCode.Created);
            int ordinaryId = int.Parse(ordinaryInsert.Headers.Location!.Segments[^1]);

            using var acceptance = await client.PostAsJsonAsync(
                "v3/dataStores/manage",
                new { name = managedName, databaseTemplate = "Minimal" }
            );
            acceptance.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await acceptance.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
            Uri location = acceptance.Headers.Location!;
            location.IsAbsoluteUri.Should().BeTrue();
            int manageId = int.Parse(location.Segments[^1]);
            location.Should().Be(new Uri(deployment.BaseAddress, "v3/dataStores/manage/" + manageId));
            JsonElement created = await Poll("Created");
            created
                .EnumerateObject()
                .Select(property => property.Name)
                .Should()
                .BeEquivalentTo(
                    "id",
                    "name",
                    "dataStoreId",
                    "dataStoreName",
                    "status",
                    "databaseTemplate",
                    "databaseName",
                    "lastRefreshed",
                    "lastModifiedDate"
                );
            created.GetProperty("name").GetString().Should().Be(managedName);
            created.GetProperty("dataStoreName").GetString().Should().Be(managedName);
            created.GetProperty("databaseTemplate").GetString().Should().Be("Minimal");
            string target = created.GetProperty("databaseName").GetString()!;
            target.Should().Be($"EdFi_Ods_{managedName}_Minimal");
            int catalogId = created.GetProperty("dataStoreId").GetInt32();
            (await provisioning.TargetExistsAsync(target)).Should().BeTrue();

            JsonElement catalog = await GetJson("v3/dataStores/" + catalogId);
            catalog.GetProperty("provider").GetString().Should().Be(routingProvider);
            string? runtime = deployment.DecryptConnection(
                Convert.FromBase64String(catalog.GetProperty("connectionString").GetString()!)
            );
            (runtime is not null).Should().BeTrue();
            deployment.RememberSecret(runtime!);
            await using var cms = await deployment.OpenAsync();
            Guid identity = await cms.ExecuteScalarAsync<Guid>(
                """SELECT "ExpectedSourceIdentity" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = manageId }
            );
            await using (var runtimeConnection = provisioning.Connection(runtime!))
            {
                await runtimeConnection.OpenAsync();
                (
                    await runtimeConnection.ExecuteScalarAsync<string>(
                        mssql ? "SELECT SUSER_SNAME();" : "SELECT CURRENT_USER;"
                    )
                )
                    .Should()
                    .Be(provisioning.Login);
                (
                    await runtimeConnection.ExecuteScalarAsync<int>(
                        mssql
                            ? "SELECT IS_SRVROLEMEMBER('sysadmin');"
                            : "SELECT CASE WHEN rolsuper THEN 1 ELSE 0 END FROM pg_roles WHERE rolname = CURRENT_USER;"
                    )
                )
                    .Should()
                    .Be(0);
                (
                    await runtimeConnection.ExecuteScalarAsync<string>(
                        """SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema";"""
                    )
                )
                    .Should()
                    .Be(provisioning.SchemaHash);
                (
                    await runtimeConnection.ExecuteScalarAsync<Guid>(
                        """SELECT "SourceIdentity" FROM dms."DataStoreIdentity";"""
                    )
                )
                    .Should()
                    .Be(identity);
            }
            foreach (var (name, id) in new[] { (ordinaryName, ordinaryId), (managedName, catalogId) })
            {
                JsonElement filtered = await GetJson("v3/dataStores?name=" + Uri.EscapeDataString(name));
                filtered.GetArrayLength().Should().Be(1);
                filtered[0].GetProperty("id").GetInt32().Should().Be(id);
            }
            using (
                var duplicate = await client.PostAsJsonAsync(
                    "v3/dataStores/manage",
                    new { name = managedName.ToUpperInvariant(), databaseTemplate = "Minimal" }
                )
            )
            {
                duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                deployment.ContainsSecret(await duplicate.Content.ReadAsStringAsync()).Should().BeFalse();
            }
            foreach (bool update in new[] { false, true })
            {
                using var conflict = update
                    ? await client.PutAsJsonAsync(
                        "v3/dataStores/" + catalogId,
                        new
                        {
                            id = catalogId,
                            name = managedName,
                            dataStoreType = "SharedInstance",
                            provider = routingProvider,
                        }
                    )
                    : await client.DeleteAsync("v3/dataStores/" + catalogId);
                conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
                string body = await conflict.Content.ReadAsStringAsync();
                body.Contains(location.AbsoluteUri, StringComparison.Ordinal).Should().BeTrue();
                deployment.ContainsSecret(body).Should().BeFalse();
            }
            using (var deletion = await client.DeleteAsync(location))
            {
                deletion.StatusCode.Should().Be(HttpStatusCode.NoContent);
                deletion.Headers.Location.Should().BeNull();
                (await deletion.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
            }
            JsonElement deleted = await Poll("Deleted");
            foreach (
                string field in new[]
                {
                    "id",
                    "name",
                    "dataStoreId",
                    "dataStoreName",
                    "databaseName",
                    "databaseTemplate",
                }
            )
            {
                deleted
                    .GetProperty(field)
                    .GetRawText()
                    .Should()
                    .Be(created.GetProperty(field).GetRawText(), "tombstone retains " + field);
            }
            deleted
                .GetProperty("lastModifiedDate")
                .GetDateTimeOffset()
                .Should()
                .BeOnOrAfter(created.GetProperty("lastModifiedDate").GetDateTimeOffset());
            deleted
                .GetProperty("lastRefreshed")
                .GetDateTimeOffset()
                .Should()
                .BeAfter(created.GetProperty("lastRefreshed").GetDateTimeOffset());
            deleted
                .GetProperty("lastRefreshed")
                .GetDateTimeOffset()
                .Should()
                .Be(deleted.GetProperty("lastModifiedDate").GetDateTimeOffset());
            (await provisioning.TargetExistsAsync(target)).Should().BeFalse();
            using (var absent = await client.GetAsync("v3/dataStores/" + catalogId))
            {
                absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            using (var repeatedDelete = await client.DeleteAsync(location))
            {
                repeatedDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            JsonElement remaining = await GetJson("v3/dataStores");
            remaining.GetArrayLength().Should().Be(1);
            remaining[0].GetProperty("id").GetInt32().Should().Be(ordinaryId);
            JsonElement history = await GetJson(
                "v3/dataStores/manage?name=" + Uri.EscapeDataString(managedName)
            );
            history.GetArrayLength().Should().Be(1);
            history[0].GetProperty("status").GetString().Should().Be("Deleted");

            // Poll only read observations; the production worker owns claims, leases, retries and settlement.
            using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (
                await cms.ExecuteScalarAsync<int>(
                    """SELECT COUNT(*) FROM dmscs."Job" WHERE "Status" = 'Completed';"""
                ) != 2
            )
            {
                await Task.Delay(100, settlement.Token);
            }
            (await cms.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."Job";""")).Should().Be(2);
            (
                await cms.ExecuteScalarAsync<int>(
                    """SELECT COUNT(*) FROM dmscs."Job" WHERE "AttemptCount" = 1 AND "FencingToken" > 0 AND "LeaseOwner" IS NULL AND "LeaseExpiresAt" IS NULL AND "FinishedAt" IS NOT NULL;"""
                )
            )
                .Should()
                .Be(2);
            (await cms.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStoreManage";"""))
                .Should()
                .Be(1);
            provisioning.OperationsAreEmpty.Should().BeTrue();
            deployment
                .ContainsSecret(deployment.Output)
                .Should()
                .BeFalse("production output must not expose credentials or tokens");

            async Task<JsonElement> GetJson(string path)
            {
                using var response = await client.GetAsync(path);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                string body = await response.Content.ReadAsStringAsync();
                deployment.ContainsSecret(body).Should().BeFalse();
                using var json = JsonDocument.Parse(body);
                return json.RootElement.Clone();
            }
            async Task<JsonElement> Poll(string expected)
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
        }
        catch
        {
            await TestContext.Progress.WriteLineAsync(deployment.RedactedOutput());
            throw;
        }
    }
}
