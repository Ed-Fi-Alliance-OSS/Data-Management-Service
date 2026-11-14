// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public abstract partial class OrdinaryDataStoreQueryTests
{
    private sealed class TestPathBaseStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, continuation) =>
                    {
                        context.Request.PathBase = context.Request.Headers["X-Test-PathBase"].ToString();
                        await continuation();
                    }
                );
                next(app);
            };
    }

    [TestCase("put", "", "")]
    [TestCase("delete", "", "/")]
    [TestCase("put", "/cms", "/")]
    [TestCase("delete", "/cms", "")]
    [TestCase("put", "/cms/", "")]
    [TestCase("delete", "/cms/", "/")]
    public async Task It_returns_a_safe_absolute_managed_conflict_even_with_false_management_configuration(
        string operation,
        string pathBase,
        string suffix
    )
    {
        // The management flag is not wired until step 5.2. This proves unconditional ordinary
        // guards with that configuration present, not the later managed endpoint feature gate.
        int id = await InsertAsync(new("ordinary"), CurrentTenantId);
        int manageId = await LinkManagedAsync(id, CurrentTenantId);
        using HttpClient client = Client();
        client.BaseAddress = new Uri("https://cms.example.test:7443");
        client.DefaultRequestHeaders.Add("X-Test-PathBase", pathBase);
        using HttpResponseMessage response =
            operation == "put"
                ? await client.PutAsJsonAsync(
                    $"/v3/dataStores/{id}{suffix}",
                    new
                    {
                        Id = id,
                        Name = "changed",
                        DataStoreType = "SQL",
                    }
                )
                : await client.DeleteAsync($"/v3/dataStores/{id}{suffix}");
        string content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, content);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        JsonObject body = JsonNode.Parse(content)!.AsObject();
        body.Select(item => item.Key)
            .Should()
            .BeEquivalentTo(
                "detail",
                "type",
                "title",
                "status",
                "correlationId",
                "validationErrors",
                "errors"
            );
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be(
                $"This data store is managed. Use https://cms.example.test:7443{pathBase.TrimEnd('/')}/v3/dataStores/manage/{manageId} to manage it."
            );
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:conflict");
        body["title"]!.GetValue<string>().Should().Be("Conflict");
        body["status"]!.GetValue<int>().Should().Be(409);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!.AsArray().Should().BeEmpty();
        content.Should().NotContain("secret.internal").And.NotContain("admin_db");
        using HttpResponseMessage read = await client.GetAsync($"/v3/dataStores/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await read.Content.ReadAsStringAsync())!["name"]!
            .GetValue<string>()
            .Should()
            .Be("ordinary");

        await UnlinkManagedAsync(manageId);
        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            $"/v3/dataStores/{id}",
            new
            {
                Id = id,
                Name = "changed",
                DataStoreType = "SQL",
            }
        );
        updated.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using HttpResponseMessage deleted = await client.DeleteAsync($"/v3/dataStores/{id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [TestCase("put", null, HttpStatusCode.Unauthorized)]
    [TestCase("delete", null, HttpStatusCode.Unauthorized)]
    [TestCase("put", "edfi_admin_api/readonly_access", HttpStatusCode.Forbidden)]
    [TestCase("delete", "edfi_admin_api/readonly_access", HttpStatusCode.Forbidden)]
    public async Task It_authorizes_before_revealing_managed_links(
        string operation,
        string? scope,
        HttpStatusCode expected
    )
    {
        int id = await InsertAsync(new("ordinary"), CurrentTenantId);
        await LinkManagedAsync(id, CurrentTenantId);
        using HttpClient client = Client(scope);
        using HttpResponseMessage response =
            operation == "put"
                ? await client.PutAsJsonAsync(
                    $"/v3/dataStores/{id}",
                    new
                    {
                        Id = id,
                        Name = "changed",
                        DataStoreType = "SQL",
                    }
                )
                : await client.DeleteAsync($"/v3/dataStores/{id}");
        response.StatusCode.Should().Be(expected);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("/v3/dataStores/manage/");
    }

    [Test]
    public async Task It_preserves_linked_PUT_validation_and_cross_tenant_not_found_precedence()
    {
        int id = await InsertAsync(new("ordinary"), CurrentTenantId);
        await LinkManagedAsync(id, CurrentTenantId);
        using HttpClient client = Client();
        foreach (
            var command in new[]
            {
                new
                {
                    Id = id,
                    Name = "",
                    DataStoreType = "SQL",
                },
                new
                {
                    Id = id + 1,
                    Name = "changed",
                    DataStoreType = "SQL",
                },
            }
        )
        {
            using HttpResponseMessage response = await client.PutAsJsonAsync($"/v3/dataStores/{id}", command);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        int otherId = await InsertAsync(new("other"), _tenantB);
        await LinkManagedAsync(otherId, _tenantB);
        using HttpResponseMessage update = await client.PutAsJsonAsync(
            $"/v3/dataStores/{otherId}",
            new
            {
                Id = otherId,
                Name = "changed",
                DataStoreType = "SQL",
            }
        );
        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using HttpResponseMessage delete = await client.DeleteAsync($"/v3/dataStores/{otherId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
