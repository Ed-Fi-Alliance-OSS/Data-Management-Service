// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class MetadataModuleTests
{
    private static readonly string[] HttpMethodNames =
    [
        "get",
        "put",
        "post",
        "delete",
        "patch",
        "head",
        "options",
        "trace",
    ];

    /// <summary>
    /// Every operation in the published document that declares an "id" path parameter, with the
    /// integer format it must carry. DMS-1337 narrowed the 11 spec-named resource identifiers to
    /// int32, so all of them are int32. Asserted as an exact set: the sweep cannot pass trivially on
    /// an empty document, a route disappearing from the document fails the test, and a single
    /// operation regressing to int64 fails it as well.
    /// The nine item routes are joined by five secondary routes that carry the same identifier.
    /// /v3/apiClients/{clientId} is a string parameter and is excluded by the "id" name filter.
    /// /v3/tenants/{id} is out of scope and only appears when multi-tenancy is enabled - it is pinned
    /// as int64 by It_should_declare_the_out_of_scope_tenant_id_path_parameter_as_int64.
    /// </summary>
    private static readonly string[] ExpectedIdPathParameters =
    [
        "GET /v3/vendors/{id} int32",
        "PUT /v3/vendors/{id} int32",
        "DELETE /v3/vendors/{id} int32",
        "GET /v3/vendors/{id}/applications int32",
        "GET /v3/applications/{id} int32",
        "PUT /v3/applications/{id} int32",
        "DELETE /v3/applications/{id} int32",
        "PUT /v3/applications/{id}/reset-credential int32",
        "PUT /v3/apiClients/{id} int32",
        "DELETE /v3/apiClients/{id} int32",
        "PUT /v3/apiClients/{id}/reset-credential int32",
        "GET /v3/claimSets/{id} int32",
        "PUT /v3/claimSets/{id} int32",
        "DELETE /v3/claimSets/{id} int32",
        "GET /v3/claimSets/{id}/export int32",
        "GET /v3/profiles/{id} int32",
        "PUT /v3/profiles/{id} int32",
        "DELETE /v3/profiles/{id} int32",
        "GET /v3/resourceClaims/{id} int32",
        "GET /v3/dataStores/{id} int32",
        "PUT /v3/dataStores/{id} int32",
        "DELETE /v3/dataStores/{id} int32",
        "GET /v3/dataStores/{id}/applications int32",
        "GET /v3/dataStoreContexts/{id} int32",
        "PUT /v3/dataStoreContexts/{id} int32",
        "DELETE /v3/dataStoreContexts/{id} int32",
        "GET /v3/dataStoreDerivatives/{id} int32",
        "PUT /v3/dataStoreDerivatives/{id} int32",
        "DELETE /v3/dataStoreDerivatives/{id} int32",
    ];

    /// <summary>
    /// Every integer-valued property of every schema the generator emits into components.schemas,
    /// with its required format. Array properties are reported by their item format.
    /// Only a subset of the DTOs reaches the document at all - request-body commands plus the
    /// response types declared through .Produces&lt;T&gt;() - so this covers exactly what is
    /// publishable, and the model identifier contract test covers the rest.
    /// The three educationOrganizationIds entries are the documented int64 exception: the draft
    /// Management API v3 spec declares Ed-Fi education organization ids int64 even though Admin API
    /// uses int32. Pinning them here means a careless sweep of the data model fails loudly.
    /// </summary>
    private static readonly string[] ExpectedSchemaIdentifierFormats =
    [
        "ApiClientCredentialsResponse.id int32",
        "ApiClientCredentialsResponse.applicationId int32",
        "ApiClientInsertCommand.applicationId int32",
        "ApiClientInsertCommand.dataStoreIds int32",
        "ApiClientResponse.id int32",
        "ApiClientResponse.applicationId int32",
        "ApiClientResponse.dataStoreIds int32",
        "ApiClientUpdateCommand.id int32",
        "ApiClientUpdateCommand.applicationId int32",
        "ApiClientUpdateCommand.dataStoreIds int32",
        "ApplicationInsertCommand.vendorId int32",
        "ApplicationInsertCommand.educationOrganizationIds int64",
        "ApplicationInsertCommand.dataStoreIds int32",
        "ApplicationInsertCommand.profileIds int32",
        "ApplicationResponse.id int32",
        "ApplicationResponse.vendorId int32",
        "ApplicationResponse.educationOrganizationIds int64",
        "ApplicationResponse.dataStoreIds int32",
        "ApplicationResponse.profileIds int32",
        "ApplicationUpdateCommand.id int32",
        "ApplicationUpdateCommand.vendorId int32",
        "ApplicationUpdateCommand.educationOrganizationIds int64",
        "ApplicationUpdateCommand.dataStoreIds int32",
        "ApplicationUpdateCommand.profileIds int32",
        "AuthorizationStrategy.id int32",
        "ClaimSetCopyCommand.originalId int32",
        "ClaimSetResourceClaimActionAuthStrategies.actionId int32",
        "ClaimSetUpdateCommand.id int32",
        "DataStoreContextInsertCommand.dataStoreId int32",
        "DataStoreContextUpdateCommand.id int32",
        "DataStoreContextUpdateCommand.dataStoreId int32",
        "DataStoreDerivativeInsertCommand.dataStoreId int32",
        "DataStoreDerivativeUpdateCommand.id int32",
        "DataStoreDerivativeUpdateCommand.dataStoreId int32",
        "DataStoreUpdateCommand.id int32",
        "ProfileUpdateCommand.id int32",
        "VendorUpdateCommand.id int32",
    ];

    [Test]
    public async Task Metadata_Specifications_Endpoint_Is_Registered()
    {
        // Arrange
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/specifications");

        // Assert
        // 200 rather than "not 404": the endpoint assembles its document from a self-request for
        // /openapi/v1.json, so a merely-registered route can still fault with a 500.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task MetadataSpecifications_Declares_Id_Parameter_As_Int32()
    {
        // Arrange
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/specifications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        // MetadataModule hand-writes this reusable parameter, so no amount of model retyping
        // reaches it. /metadata/specifications is the artifact the root metadata endpoint
        // advertises, which makes it the one integrators read.
        var idParameter = doc
            .RootElement.GetProperty("components")
            .GetProperty("parameters")
            .GetProperty("id");

        idParameter.GetProperty("in").GetString().Should().Be("path");
        TypeIncludes(idParameter.GetProperty("schema").GetProperty("type"), "integer")
            .Should()
            .BeTrue("the reusable id parameter describes a numeric resource identifier");
        idParameter.GetProperty("schema").GetProperty("format").GetString().Should().Be("int32");
    }

    [Test]
    public async Task OpenApi_Declares_Resource_Identifiers_As_Int32()
    {
        // Arrange
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Act
        var doc = await FetchOpenApiDocumentAsync(client);
        var paths = doc.RootElement.GetProperty("paths");

        // Assert
        var actualIdPathParameters = new List<string>();

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!HttpMethodNames.Contains(operation.Name))
                {
                    continue;
                }

                if (!operation.Value.TryGetProperty("parameters", out var parameters))
                {
                    continue;
                }

                foreach (var parameter in parameters.EnumerateArray())
                {
                    if (
                        parameter.GetProperty("name").GetString() != "id"
                        || parameter.GetProperty("in").GetString() != "path"
                    )
                    {
                        continue;
                    }

                    var schema = parameter.GetProperty("schema");
                    TypeIncludes(schema.GetProperty("type"), "integer")
                        .Should()
                        .BeTrue(
                            $"{operation.Name.ToUpperInvariant()} {path.Name} declares a numeric resource identifier"
                        );

                    actualIdPathParameters.Add(
                        $"{operation.Name.ToUpperInvariant()} {path.Name.TrimEnd('/')} {schema.GetProperty("format").GetString()}"
                    );
                }
            }
        }

        actualIdPathParameters
            .Should()
            .BeEquivalentTo(
                ExpectedIdPathParameters,
                "every published id path parameter is an int32 resource identifier, on every path and "
                    + "every operation that declares one"
            );
    }

    [Test]
    public async Task OpenApi_Declares_Schema_Identifier_Properties_As_Int32()
    {
        // Arrange
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Act
        var doc = await FetchOpenApiDocumentAsync(client);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Assert
        var actualFormats = new List<string>();

        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                // Collections declare the identifier type on their items, scalars on themselves.
                var valueSchema = property.Value.TryGetProperty("items", out var items)
                    ? items
                    : property.Value;

                if (
                    !valueSchema.TryGetProperty("type", out var type)
                    || !TypeIncludes(type, "integer")
                    || !valueSchema.TryGetProperty("format", out var format)
                )
                {
                    continue;
                }

                actualFormats.Add($"{schema.Name}.{property.Name} {format.GetString()}");
            }
        }

        actualFormats
            .Should()
            .BeEquivalentTo(
                ExpectedSchemaIdentifierFormats,
                "resource identifiers are int32 in the published schemas, and education-organization "
                    + "ids remain int64"
            );
    }

    [Test]
    public async Task OpenApi_Registers_Actions_And_AuthorizationStrategies_As_Collection_Routes()
    {
        // Arrange
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Act
        var doc = await FetchOpenApiDocumentAsync(client);
        var pathMap = doc
            .RootElement.GetProperty("paths")
            .EnumerateObject()
            .ToDictionary(p => p.Name.TrimEnd('/').ToLowerInvariant(), p => p.Value);

        // Assert
        // Route presence only. ActionsModule and AuthorizationStrategiesModule map collection-only
        // GETs that return untyped IResult with no .Produces<T>(), so the generator emits neither an
        // item route with an id parameter nor a response schema for either resource. Action.Id is
        // already int and is covered by the model identifier contract test.
        foreach (var path in new[] { "/v3/actions", "/v3/authorizationStrategies" })
        {
            pathMap.Should().ContainKey(path.ToLowerInvariant());
            pathMap[path.ToLowerInvariant()]
                .TryGetProperty("get", out _)
                .Should()
                .BeTrue($"GET {path} should exist");
        }
    }

    [Test]
    public async Task OpenApi_Declares_The_Out_Of_Scope_Tenant_Id_As_Int64()
    {
        // Arrange
        // Tenants are out of scope for DMS-1337 - no numeric tenant identifier exists anywhere else
        // in the Ed-Fi platform to align with - so TenantModule keeps a long id. Pinning it here is
        // what makes a careless sweep of the frontend handlers fail loudly.
        // TenantModule is only registered when multi-tenancy is enabled, and with multi-tenancy on,
        // TenantResolutionMiddleware requires a resolvable Tenant header on every request that is
        // not tenant-agnostic, including /openapi/v1.json.
        var tenantRepository = A.Fake<ITenantRepository>();
        A.CallTo(() => tenantRepository.GetTenantByName("test-tenant"))
            .Returns(new TenantGetByNameResult.Success(new TenantResponse { Id = 1, Name = "test-tenant" }));

        await using var factory = CreateFactory(multiTenancy: true, tenantRepository: tenantRepository);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Tenant", "test-tenant");

        // Act
        var doc = await FetchOpenApiDocumentAsync(client);

        // Assert
        var schema = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/v3/tenants/{id}")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter =>
                parameter.GetProperty("name").GetString() == "id"
                && parameter.GetProperty("in").GetString() == "path"
            )
            .GetProperty("schema");

        schema.GetProperty("format").GetString().Should().Be("int64");
    }

    private static async Task<System.Text.Json.JsonDocument> FetchOpenApiDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> CreateFactory(
        bool multiTenancy = false,
        ITenantRepository? tenantRepository = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        // The application reset-credential route is registered only when this flag is
                        // set, and it is false in both appsettings.json and appsettings.Test.json, so
                        // without this the route is absent from the document under test.
                        ["AppSettings:EnableApplicationResetEndpoint"] = "true",
                        ["AppSettings:MultiTenancy"] = multiTenancy.ToString(),
                    }
                )
            );
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory, TestServerHttpClientFactory>();

                if (tenantRepository is not null)
                {
                    services.AddTransient(_ => tenantRepository);
                }
            });
        });
    }

    /// <summary>
    /// MetadataModule assembles /metadata/specifications by requesting /openapi/v1.json over HTTP
    /// from itself. The default IHttpClientFactory client uses a real socket handler, which cannot
    /// reach the in-memory TestServer, so the endpoint faults under WebApplicationFactory. Routing
    /// the client through the TestServer handler resolves the self-request in-process.
    /// </summary>
    private sealed class TestServerHttpClientFactory(IServer server) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => ((TestServer)server).CreateClient();
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
