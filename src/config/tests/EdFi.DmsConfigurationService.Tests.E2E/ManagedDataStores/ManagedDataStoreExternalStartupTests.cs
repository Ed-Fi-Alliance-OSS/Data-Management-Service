// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.ManagedDataStores;

[TestFixture, NonParallelizable, Category("ManagedDataStoreExternalHttp")]
[Explicit(
    "Creates isolated catalogs and launches a production CMS child process; requires the managed HTTP lane settings."
)]
public class Given_external_managed_data_store_startup
{
    [TestCase(false), TestCase(true)]
    public async Task It_requires_explicit_opt_out_and_enforces_real_auth_before_the_disabled_gate(bool mssql)
    {
        await using var deployment = await ManagedDataStoreExternalDeployment.CreateAsync(mssql);
        try
        {
            deployment.Start(management: null);
            (await deployment.WaitForExitAsync()).Should().NotBe(0);
            deployment
                .Output.Contains(
                    "AppSettings:EnableDataStoreManagement requires DmsDataStoreProvisioning:Enabled=true",
                    StringComparison.Ordinal
                )
                .Should()
                .BeTrue("the omitted shipped setting must fail for the management configuration reason");

            deployment.Start(management: false);
            using var client = new HttpClient
            {
                BaseAddress = deployment.BaseAddress,
                Timeout = TimeSpan.FromSeconds(15),
            };
            await deployment.WaitForHttpAsync(client);
            string administrator = "http-admin-" + Guid.NewGuid().ToString("N");
            string limited = "http-limited-" + Guid.NewGuid().ToString("N");
            await Register(administrator);
            await Register(limited);

            // Provision the limited client's permissions as fixture data. Token issuance, signature
            // verification and role/scope authorization below all run in the production HTTP process.
            const string limitedScope = "edfi_admin_api/authMetadata_readonly_access";
            await using var connection = await deployment.OpenAsync();
            object permissions = mssql
                ? JsonSerializer.Serialize(new[] { limitedScope })
                : new[] { limitedScope };
            (
                await connection.ExecuteAsync(
                    """UPDATE dmscs."OpenIddictApplication" SET "Permissions" = @Permissions WHERE "ClientId" = @ClientId;""",
                    new { Permissions = permissions, ClientId = limited }
                )
            )
                .Should()
                .Be(1);
            string adminToken = await Token(administrator, "edfi_admin_api/full_access");
            string limitedToken = await Token(limited, limitedScope);
            string canary = "untrusted-" + Guid.NewGuid().ToString("N");
            deployment.RememberSecret(canary);

            foreach (string? token in new string?[] { null, limitedToken, adminToken })
            {
                foreach (
                    var (method, path) in new[]
                    {
                        (HttpMethod.Get, "v3/dataStores/manage?limit=invalid"),
                        (HttpMethod.Get, "v3/dataStores/manage/not-an-id"),
                        (HttpMethod.Post, "v3/dataStores/manage"),
                        (HttpMethod.Delete, "v3/dataStores/manage/not-an-id"),
                    }
                )
                {
                    using var request = new HttpRequestMessage(method, path);
                    if (token is not null)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }
                    if (method == HttpMethod.Post)
                    {
                        request.Content = new StringContent(
                            "{\"password\":\"" + canary,
                            Encoding.UTF8,
                            "application/json"
                        );
                    }
                    using var response = await client.SendAsync(request);
                    HttpStatusCode expected = token is null
                        ? HttpStatusCode.Unauthorized
                        : HttpStatusCode.Forbidden;
                    if (token == adminToken)
                    {
                        expected = HttpStatusCode.BadRequest;
                    }
                    response.StatusCode.Should().Be(expected);
                    string body = await response.Content.ReadAsStringAsync();
                    deployment
                        .ContainsSecret(body)
                        .Should()
                        .BeFalse("error responses must not echo credentials or the request canary");
                    response.Headers.Location.Should().BeNull();
                    if (token == adminToken)
                    {
                        body.Contains(
                                "Data store management is disabled by AppSettings:EnableDataStoreManagement.",
                                StringComparison.Ordinal
                            )
                            .Should()
                            .BeTrue(
                                "the feature gate must precede malformed body, query and identifier parsing"
                            );
                    }
                }
            }

            using var ordinary = new HttpRequestMessage(HttpMethod.Get, "v3/dataStores");
            ordinary.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var ordinaryResponse = await client.SendAsync(ordinary);
            ordinaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ordinaryResponse.Content.ReadAsStringAsync()).Should().Be("[]");
            (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStoreManage";"""))
                .Should()
                .Be(0);
            (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."Job";"""))
                .Should()
                .Be(0);
            deployment
                .ContainsSecret(deployment.Output)
                .Should()
                .BeFalse("captured production output must not expose the test secrets");

            async Task Register(string id)
            {
                using var response = await client.PostAsync(
                    "connect/register",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["ClientId"] = id,
                            ["ClientSecret"] = deployment.ClientSecret,
                            ["DisplayName"] = "Managed HTTP qualification",
                        }
                    )
                );
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                deployment.ContainsSecret(await response.Content.ReadAsStringAsync()).Should().BeFalse();
            }
            async Task<string> Token(string id, string scope)
            {
                using var response = await client.PostAsync(
                    "connect/token",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["client_id"] = id,
                            ["client_secret"] = deployment.ClientSecret,
                            ["grant_type"] = "client_credentials",
                            ["scope"] = scope,
                        }
                    )
                );
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                string token = json.RootElement.GetProperty("access_token").GetString()!;
                deployment.RememberSecret(token);
                return token;
            }
        }
        catch
        {
            await TestContext.Progress.WriteLineAsync(deployment.RedactedOutput());
            throw;
        }
    }
}
