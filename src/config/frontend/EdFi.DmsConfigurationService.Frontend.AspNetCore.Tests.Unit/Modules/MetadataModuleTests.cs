// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class MetadataModuleTests
{
    [Test]
    public async Task Metadata_Specifications_Endpoint_Is_Registered()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(cfg =>
                cfg.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
                )
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/specifications");

        // Assert
        // Endpoint should not return 404 (Not Found) - it exists and is registered
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task OpenApi_V1_Endpoint_Is_Registered()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/openapi/v1.json");

        // Assert
        // Endpoint should not return 404 (Not Found) - it exists and is registered
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task OpenApi_Collection_Endpoints_Expose_Paging_And_Sort_Params()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        // Normalize path keys: trim trailing slashes and use lower-case for lookup
        var pathMap = paths
            .EnumerateObject()
            .ToDictionary(p => p.Name.TrimEnd('/').ToLowerInvariant(), p => p.Value);

        var collectionEndpoints = new[]
        {
            "/v2/vendors",
            "/v2/applications",
            "/v2/apiClients",
            "/v2/dmsInstances",
            "/v2/claimSets",
            "/v2/tenants",
            "/v2/profiles",
            "/v2/dmsInstanceDerivatives",
            "/v2/dmsinstanceroutecontexts",
        };

        var requiredParams = new[] { "offset", "limit", "orderby", "direction" };

        foreach (var path in collectionEndpoints)
        {
            var normalized = path.TrimEnd('/').ToLowerInvariant();
            if (!pathMap.TryGetValue(normalized, out var pathItem))
            {
                await TestContext.Out.WriteLineAsync(
                    $"Skipping {path} because it is not registered in OpenAPI spec"
                );
                continue;
            }
            pathItem.TryGetProperty("get", out var getOp).Should().BeTrue($"GET {path} should exist");
            getOp
                .TryGetProperty("parameters", out var parameters)
                .Should()
                .BeTrue($"GET {path} should have parameters");

            var paramMap = parameters
                .EnumerateArray()
                .ToDictionary(p => p.GetProperty("name").GetString()!.ToLowerInvariant(), p => p);

            foreach (var required in requiredParams)
            {
                paramMap
                    .Should()
                    .ContainKey(required, $"GET {path} should expose '{required}' as a query parameter");

                // Verify parameter has description
                var param = paramMap[required];
                param
                    .TryGetProperty("description", out var description)
                    .Should()
                    .BeTrue($"GET {path} parameter '{required}' should have a description");
                description.GetString().Should().NotBeNullOrWhiteSpace();

                // Verify offset and limit are integers
                if (required is "offset" or "limit")
                {
                    param
                        .TryGetProperty("schema", out var schema)
                        .Should()
                        .BeTrue($"GET {path} parameter '{required}' should have schema");
                    schema
                        .TryGetProperty("type", out var type)
                        .Should()
                        .BeTrue($"GET {path} parameter '{required}' schema should have type");
                    TypeIncludes(type, "integer")
                        .Should()
                        .BeTrue($"GET {path} parameter '{required}' schema type should include integer");

                    if (required == "limit")
                    {
                        schema
                            .TryGetProperty("minimum", out var minimum)
                            .Should()
                            .BeTrue($"GET {path} parameter '{required}' schema should have minimum");
                        minimum.GetInt32().Should().Be(1);
                    }
                }

                // Verify direction parameter has description mentioning allowed values
                if (required == "direction")
                {
                    param.TryGetProperty("description", out var dirDescription).Should().BeTrue();
                    var dirDescText = dirDescription.GetString()!.ToLowerInvariant();
                    dirDescText.Should().Contain("asc");
                    dirDescText.Should().Contain("desc");
                }
            }
        }
    }

    [Test]
    public async Task OpenApi_Profile_Collection_Endpoint_Exposes_Filter_Params()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        // Normalize path keys: trim trailing slashes and use lower-case for lookup
        var pathMap = paths
            .EnumerateObject()
            .ToDictionary(p => p.Name.TrimEnd('/').ToLowerInvariant(), p => p.Value);

        var profilesKey = "/v2/profiles".TrimEnd('/').ToLowerInvariant();
        pathMap.Should().ContainKey(profilesKey, "path /v2/profiles should exist in OpenAPI spec");
        var pathItem = pathMap[profilesKey];
        pathItem.TryGetProperty("get", out var getOp).Should().BeTrue("GET /v2/profiles should exist");
        getOp
            .TryGetProperty("parameters", out var parameters)
            .Should()
            .BeTrue("GET /v2/profiles should have parameters");

        var paramMap = parameters
            .EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!.ToLowerInvariant(), p => p);

        foreach (var required in new[] { "offset", "limit", "orderby", "direction", "id", "name" })
        {
            paramMap
                .Should()
                .ContainKey(required, $"GET /v2/profiles should expose '{required}' as a query parameter");
            paramMap[required]
                .TryGetProperty("description", out var description)
                .Should()
                .BeTrue($"GET /v2/profiles parameter '{required}' should have a description");
            description.GetString().Should().NotBeNullOrWhiteSpace();
        }

        paramMap["id"].TryGetProperty("schema", out var idSchema).Should().BeTrue();
        idSchema.TryGetProperty("type", out var idType).Should().BeTrue();
        TypeIncludes(idType, "integer")
            .Should()
            .BeTrue("GET /v2/profiles parameter 'id' schema should include integer");

        paramMap["name"].TryGetProperty("schema", out var nameSchema).Should().BeTrue();
        nameSchema.TryGetProperty("type", out var nameType).Should().BeTrue();
        TypeIncludes(nameType, "string")
            .Should()
            .BeTrue("GET /v2/profiles parameter 'name' schema should include string");
    }

    [Test]
    public async Task OpenApi_ApiClient_Response_Schemas_Expose_Story_Fields()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var pathMap = doc
            .RootElement.GetProperty("paths")
            .EnumerateObject()
            .ToDictionary(p => p.Name.TrimEnd('/').ToLowerInvariant(), p => p.Value);

        var apiClientsPath = "/v2/apiClients".TrimEnd('/').ToLowerInvariant();
        pathMap.Should().ContainKey(apiClientsPath, "path /v2/apiClients should exist in OpenAPI spec");
        var pathItem = pathMap[apiClientsPath];

        // Assert
        var getProperties = ResolveJsonResponseSchemaProperties(doc, pathItem, "get", "200");
        getProperties.Should().ContainKey("name");
        getProperties.Should().ContainKey("clientUuid");

        var postProperties = ResolveJsonResponseSchemaProperties(doc, pathItem, "post", "201");
        postProperties.Should().ContainKeys("applicationId", "name", "key", "secret");
    }

    private static bool TypeIncludes(System.Text.Json.JsonElement type, string expectedType)
    {
        return type.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => type.GetString() == expectedType,
            System.Text.Json.JsonValueKind.Array => type.EnumerateArray()
                .Any(item =>
                    item.ValueKind == System.Text.Json.JsonValueKind.String
                    && item.GetString() == expectedType
                ),
            _ => false,
        };
    }

    private static Dictionary<string, System.Text.Json.JsonElement> ResolveJsonResponseSchemaProperties(
        System.Text.Json.JsonDocument doc,
        System.Text.Json.JsonElement pathItem,
        string method,
        string statusCode
    )
    {
        pathItem
            .TryGetProperty(method, out var operation)
            .Should()
            .BeTrue($"{method.ToUpperInvariant()} should exist");
        operation
            .GetProperty("responses")
            .TryGetProperty(statusCode, out var response)
            .Should()
            .BeTrue($"{method.ToUpperInvariant()} should define a {statusCode} response");
        response.TryGetProperty("content", out var content).Should().BeTrue("response should define content");
        content
            .TryGetProperty("application/json", out var jsonContent)
            .Should()
            .BeTrue("response should define application/json content");
        jsonContent.TryGetProperty("schema", out var schema).Should().BeTrue("content should define schema");

        var objectSchema = ResolveObjectSchema(doc, schema);
        objectSchema
            .TryGetProperty("properties", out var properties)
            .Should()
            .BeTrue("schema should define properties");

        return properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    }

    private static System.Text.Json.JsonElement ResolveObjectSchema(
        System.Text.Json.JsonDocument doc,
        System.Text.Json.JsonElement schema
    )
    {
        if (schema.TryGetProperty("type", out var type) && TypeIncludes(type, "array"))
        {
            schema.TryGetProperty("items", out var items).Should().BeTrue("array schema should define items");
            return ResolveObjectSchema(doc, items);
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            var referenceParts = reference.GetString()!.Split('/');
            var schemaName = referenceParts[^1];
            return doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        }

        return schema;
    }
}
