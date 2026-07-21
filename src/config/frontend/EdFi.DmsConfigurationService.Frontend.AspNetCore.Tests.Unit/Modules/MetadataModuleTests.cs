// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            "/v3/vendors",
            "/v3/applications",
            "/v3/apiClients",
            "/v3/dataStores",
            "/v3/claimSets",
            "/v3/tenants",
            "/v3/profiles",
            "/v3/dataStoreDerivatives",
            "/v3/dataStoreContexts",
            "/v3/resourceClaims",
            "/v3/resourceClaimActions",
            "/v3/resourceClaimActionAuthStrategies",
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

        var profilesKey = "/v3/profiles".TrimEnd('/').ToLowerInvariant();
        pathMap.Should().ContainKey(profilesKey, "path /v3/profiles should exist in OpenAPI spec");
        var pathItem = pathMap[profilesKey];
        pathItem.TryGetProperty("get", out var getOp).Should().BeTrue("GET /v3/profiles should exist");
        getOp
            .TryGetProperty("parameters", out var parameters)
            .Should()
            .BeTrue("GET /v3/profiles should have parameters");

        var paramMap = parameters
            .EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!.ToLowerInvariant(), p => p);

        foreach (var required in new[] { "offset", "limit", "orderby", "direction", "id", "name" })
        {
            paramMap
                .Should()
                .ContainKey(required, $"GET /v3/profiles should expose '{required}' as a query parameter");
            paramMap[required]
                .TryGetProperty("description", out var description)
                .Should()
                .BeTrue($"GET /v3/profiles parameter '{required}' should have a description");
            description.GetString().Should().NotBeNullOrWhiteSpace();
        }

        paramMap["id"].TryGetProperty("schema", out var idSchema).Should().BeTrue();
        idSchema.TryGetProperty("type", out var idType).Should().BeTrue();
        TypeIncludes(idType, "integer")
            .Should()
            .BeTrue("GET /v3/profiles parameter 'id' schema should include integer");

        paramMap["name"].TryGetProperty("schema", out var nameSchema).Should().BeTrue();
        nameSchema.TryGetProperty("type", out var nameType).Should().BeTrue();
        TypeIncludes(nameType, "string")
            .Should()
            .BeTrue("GET /v3/profiles parameter 'name' schema should include string");
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

        var apiClientsPath = "/v3/apiClients".TrimEnd('/').ToLowerInvariant();
        pathMap.Should().ContainKey(apiClientsPath, "path /v3/apiClients should exist in OpenAPI spec");
        var pathItem = pathMap[apiClientsPath];

        // Assert
        var getProperties = ResolveJsonResponseSchemaProperties(doc, pathItem, "get", "200");
        getProperties.Should().ContainKey("name");
        getProperties.Should().ContainKey("clientUuid");

        var postProperties = ResolveJsonResponseSchemaProperties(doc, pathItem, "post", "201");
        postProperties.Should().ContainKeys("applicationId", "name", "key", "secret");

        var apiClientByIdPath = "/v3/apiClients/{clientId}".TrimEnd('/').ToLowerInvariant();
        pathMap
            .Should()
            .ContainKey(apiClientByIdPath, "path /v3/apiClients/{clientId} should exist in OpenAPI spec");
        var pathItemById = pathMap[apiClientByIdPath];

        var getByIdProperties = ResolveJsonResponseSchemaProperties(doc, pathItemById, "get", "200");
        getByIdProperties.Should().ContainKey("name");
        getByIdProperties.Should().ContainKey("clientUuid");

        var resetCredentialPath = "/v3/apiClients/{id}/reset-credential".TrimEnd('/').ToLowerInvariant();
        pathMap
            .Should()
            .ContainKey(
                resetCredentialPath,
                "path /v3/apiClients/{id}/reset-credential should exist in OpenAPI spec"
            );
        var pathItemResetCred = pathMap[resetCredentialPath];

        var resetCredProperties = ResolveJsonResponseSchemaProperties(doc, pathItemResetCred, "put", "200");
        resetCredProperties.Should().ContainKeys("applicationId", "name", "key", "secret");
    }

    [Test]
    public async Task OpenApi_ApplicationResponse_Schema_Has_Enabled_Boolean_Property()
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

        // Navigate: components -> schemas -> ApplicationResponse -> properties -> enabled
        doc.RootElement.TryGetProperty("components", out var components)
            .Should()
            .BeTrue("OpenAPI doc must have components");
        components.TryGetProperty("schemas", out var schemas).Should().BeTrue("components must have schemas");
        schemas
            .TryGetProperty("ApplicationResponse", out var appSchema)
            .Should()
            .BeTrue("schemas must include ApplicationResponse");
        appSchema
            .TryGetProperty("properties", out var properties)
            .Should()
            .BeTrue("ApplicationResponse must have properties");
        properties
            .TryGetProperty("enabled", out var enabledProp)
            .Should()
            .BeTrue("ApplicationResponse must have 'enabled' property");
        enabledProp.TryGetProperty("type", out var enabledType).Should().BeTrue("'enabled' must have a type");
        TypeIncludes(enabledType, "boolean")
            .Should()
            .BeTrue("ApplicationResponse.enabled should be of type boolean in OpenAPI schema");
    }

    [Test]
    public async Task OpenApi_Vendor_Post_Response_Documents_Location_Header()
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

        pathMap.Should().ContainKey("/v3/vendors", "path /v3/vendors should exist in OpenAPI spec");
        var pathItem = pathMap["/v3/vendors"];

        pathItem.TryGetProperty("post", out var postOp).Should().BeTrue("POST /v3/vendors should exist");
        var responses = postOp.GetProperty("responses");

        responses
            .TryGetProperty("201", out _)
            .Should()
            .BeTrue("POST /v3/vendors should define a 201 response for new resources");

        responses
            .TryGetProperty("200", out _)
            .Should()
            .BeTrue("POST /v3/vendors should define a 200 response for updated resources");

        foreach (var code in new[] { "201", "200" })
        {
            responses.TryGetProperty(code, out var codeResponse).Should().BeTrue();
            codeResponse
                .TryGetProperty("headers", out var headers)
                .Should()
                .BeTrue($"{code} response should define headers");
            headers
                .TryGetProperty("Location", out var locationHeader)
                .Should()
                .BeTrue($"{code} response headers should include Location");
            locationHeader.GetProperty("required").GetBoolean().Should().BeTrue();
            locationHeader.GetProperty("schema").GetProperty("type").GetString().Should().Be("string");
            locationHeader.GetProperty("schema").GetProperty("format").GetString().Should().Be("uri");
            locationHeader.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            codeResponse
                .TryGetProperty("content", out _)
                .Should()
                .BeFalse($"{code} response body should be empty per CMS-GAP-009");
        }
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

/// <summary>
/// The post-processed /metadata/specifications document must reference the reusable Ed-Fi Problem Details
/// responses from applicable operations, document the OAuth protocol endpoints with their application/json
/// error schema (not Ed-Fi Problem Details), and leave success responses intact.
/// </summary>
[TestFixture]
public class Given_The_Metadata_Specifications_Document
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private JsonNode _document = null!;

    [SetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            // /metadata/specifications self-fetches /openapi/v1.json over HTTP; route that call through
            // the in-memory test server so the post-processed document can be asserted.
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IHttpClientFactory>(
                    new TestServerHttpClientFactory(() => _factory.Server.CreateHandler())
                )
            );
        });
        _client = _factory.CreateClient();

        var response = await _client.GetAsync("/metadata/specifications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        _document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private JsonObject Components => _document["components"]!.AsObject();

    private JsonObject FindOperation(string pathContains, bool withPathParameter, string method)
    {
        var paths = _document["paths"]!.AsObject();
        var match = paths.FirstOrDefault(p =>
            p.Key.Contains(pathContains, StringComparison.OrdinalIgnoreCase)
            && p.Key.Contains('{') == withPathParameter
            && p.Value!.AsObject().ContainsKey(method)
        );
        match
            .Value.Should()
            .NotBeNull(
                $"a {method.ToUpperInvariant()} operation for a path containing '{pathContains}' "
                    + $"{(withPathParameter ? "with" : "without")} a path parameter should exist"
            );
        return match.Value!.AsObject()[method]!.AsObject();
    }

    private static string? RefOf(JsonObject operation, string status) =>
        operation["responses"]?[status]?["$ref"]?.GetValue<string>();

    private JsonObject FindAnyOperation(string pathContains, string method)
    {
        var paths = _document["paths"]!.AsObject();
        var match = paths.FirstOrDefault(p =>
            p.Key.Contains(pathContains, StringComparison.OrdinalIgnoreCase)
            && p.Value!.AsObject().ContainsKey(method)
        );
        match
            .Value.Should()
            .NotBeNull(
                $"a {method.ToUpperInvariant()} operation for a path containing '{pathContains}' should exist"
            );
        return match.Value!.AsObject()[method]!.AsObject();
    }

    [Test]
    public void It_marks_the_problem_details_required_members()
    {
        var required = Components["schemas"]!["ProblemDetails"]!["required"]!
            .AsArray()
            .Select(n => n!.GetValue<string>());

        // The Ed-Fi contract always includes all seven members (validationErrors {} and errors [] when
        // empty), so the published schema marks every one required.
        required
            .Should()
            .BeEquivalentTo(
                "type",
                "title",
                "detail",
                "status",
                "correlationId",
                "validationErrors",
                "errors"
            );

        var properties = Components["schemas"]!["ProblemDetails"]!["properties"]!.AsObject();
        properties
            .Should()
            .ContainKeys("type", "title", "detail", "status", "correlationId", "validationErrors", "errors");
    }

    [Test]
    public void It_publishes_a_distinct_oauth_error_schema()
    {
        var properties = Components["schemas"]!["OAuthError"]!["properties"]!.AsObject();
        properties.Should().ContainKeys("error", "error_description");
    }

    [Test]
    public void It_defines_the_reusable_problem_details_responses_including_415()
    {
        var responses = Components["responses"]!.AsObject();
        string[] problemDetailsResponses =
        [
            "BadRequest",
            "Unauthorized",
            "Forbidden",
            "NotFound",
            "Conflict",
            "InternalServerError",
            "UnsupportedMediaType",
        ];
        responses.Should().ContainKeys(problemDetailsResponses);
        foreach (var name in problemDetailsResponses)
        {
            responses[name]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!
                .GetValue<string>()
                .Should()
                .Be("#/components/schemas/ProblemDetails");
        }
    }

    [Test]
    public void It_defines_the_oauth_error_response_as_application_json()
    {
        Components["responses"]!["OAuthError"]!["content"]!["application/json"]!["schema"]!["$ref"]!
            .GetValue<string>()
            .Should()
            .Be("#/components/schemas/OAuthError");
    }

    [Test]
    public void It_references_problem_details_from_a_resource_get_by_id()
    {
        var operation = FindOperation("/v3/vendors", withPathParameter: true, "get");
        RefOf(operation, "401").Should().Be("#/components/responses/Unauthorized");
        RefOf(operation, "403").Should().Be("#/components/responses/Forbidden");
        RefOf(operation, "404").Should().Be("#/components/responses/NotFound");
        RefOf(operation, "500").Should().Be("#/components/responses/InternalServerError");
    }

    [Test]
    public void It_references_write_error_responses_from_a_resource_post()
    {
        var operation = FindOperation("/v3/vendors", withPathParameter: false, "post");
        RefOf(operation, "400").Should().Be("#/components/responses/BadRequest");
        RefOf(operation, "409").Should().Be("#/components/responses/Conflict");
        RefOf(operation, "415").Should().Be("#/components/responses/UnsupportedMediaType");
    }

    [Test]
    public void It_documents_the_token_endpoint_with_the_oauth_error_shape()
    {
        var operation = FindOperation("/connect/token", withPathParameter: true, "post");
        RefOf(operation, "400").Should().Be("#/components/responses/OAuthError");
        RefOf(operation, "401").Should().Be("#/components/responses/OAuthError");
        RefOf(operation, "503").Should().Be("#/components/responses/OAuthError");
    }

    [Test]
    public void It_does_not_document_the_token_endpoint_as_ed_fi_problem_details()
    {
        var operation = FindOperation("/connect/token", withPathParameter: true, "post");
        var references = operation["responses"]!
            .AsObject()
            .Select(r => r.Value?["$ref"]?.GetValue<string>())
            .Where(r => r is not null);
        references.Should().OnlyContain(r => r == "#/components/responses/OAuthError");
    }

    [Test]
    public void It_preserves_success_response_metadata()
    {
        var operation = FindOperation("/v3/vendors", withPathParameter: false, "post");
        operation["responses"]!.AsObject().Should().ContainKey("201");
    }

    [Test]
    public void It_references_problem_details_from_a_management_operation()
    {
        var operation = FindAnyOperation("/management", "post");
        RefOf(operation, "401").Should().Be("#/components/responses/Unauthorized");
        RefOf(operation, "403").Should().Be("#/components/responses/Forbidden");
        RefOf(operation, "500").Should().Be("#/components/responses/InternalServerError");
    }

    [Test]
    public void It_references_bad_request_from_a_collection_get()
    {
        var operation = FindOperation("/v3/vendors", withPathParameter: false, "get");
        RefOf(operation, "400").Should().Be("#/components/responses/BadRequest");
    }

    [Test]
    public void It_defines_the_reusable_bad_gateway_response()
    {
        var responses = Components["responses"]!.AsObject();
        responses.Should().ContainKey("BadGateway");
        responses["BadGateway"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!
            .GetValue<string>()
            .Should()
            .Be("#/components/schemas/ProblemDetails");
    }

    [Test]
    public void It_defines_the_reusable_method_not_allowed_response()
    {
        // The runtime returns urn:ed-fi:api:method-not-allowed Problem Details for a wrong-method
        // request, so the reusable component is published even though no operation references it (a
        // method mismatch has no operation to attach it to; per-operation documentation is DMS-1293).
        var responses = Components["responses"]!.AsObject();
        responses.Should().ContainKey("MethodNotAllowed");
        responses["MethodNotAllowed"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!
            .GetValue<string>()
            .Should()
            .Be("#/components/schemas/ProblemDetails");
    }

    [Test]
    public void It_references_bad_gateway_from_registration()
    {
        var operation = FindAnyOperation("/connect/register", "post");
        RefOf(operation, "502").Should().Be("#/components/responses/BadGateway");
    }

    [Test]
    public void It_documents_introspection_and_revocation_with_the_oauth_error_shape()
    {
        RefOf(FindAnyOperation("/connect/introspect", "post"), "400")
            .Should()
            .Be("#/components/responses/OAuthError");
        RefOf(FindAnyOperation("/connect/revoke", "post"), "400")
            .Should()
            .Be("#/components/responses/OAuthError");
    }
}

/// <summary>
/// In multi-tenant mode the /metadata/specifications handler self-fetches /openapi/v1.json over HTTP.
/// That nested request must carry the same Tenant header as the incoming request, or tenant resolution
/// rejects it and the outer request fails with a 500. Proves the Tenant header is forwarded so the nested
/// request resolves against the same tenant and the post-processed document is returned with a 200.
/// </summary>
[TestFixture]
public class Given_The_Metadata_Specifications_Document_In_Multi_Tenant_Mode
{
    private const string TenantName = "test-tenant";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private ITenantRepository _tenantRepository = null!;
    private HttpResponseMessage _response = null!;
    private JsonNode _document = null!;

    [SetUp]
    public async Task Setup()
    {
        _tenantRepository = A.Fake<ITenantRepository>();
        A.CallTo(() => _tenantRepository.GetTenantByName(TenantName))
            .Returns(new TenantGetByNameResult.Success(new TenantResponse { Id = 1, Name = TenantName }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(cfg =>
                cfg.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
                )
            );
            // Route the internal /openapi/v1.json fetch back through the in-memory test server, and resolve
            // the tenant through the fake repository so both the outer and nested requests pass tenant
            // resolution.
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(
                    new TestServerHttpClientFactory(() => _factory.Server.CreateHandler())
                );
                services.AddSingleton(_tenantRepository);
            });
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("Tenant", TenantName);

        _response = await _client.GetAsync("/metadata/specifications");
        _document = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_returns_200_with_the_json_content_type()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.OK);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Test]
    public void It_returns_the_post_processed_configuration_service_document()
    {
        _document["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi API Configuration Service API");
        _document["components"]!["schemas"]!.AsObject().Should().ContainKey("ProblemDetails");
    }

    [Test]
    public void It_resolves_the_same_tenant_for_the_outer_and_nested_requests() =>
        // The outer /metadata/specifications request and the nested /openapi/v1.json request each resolve
        // the tenant once. A dropped Tenant header would fail the nested request's resolution before the
        // repository is consulted, and surface as a 500 on the outer request.
        A.CallTo(() => _tenantRepository.GetTenantByName(TenantName)).MustHaveHappenedTwiceExactly();
}

// Routes the module's internal /openapi/v1.json fetch through the in-memory test server.
file sealed class TestServerHttpClientFactory(Func<HttpMessageHandler> handlerFactory) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handlerFactory(), disposeHandler: false);
}
