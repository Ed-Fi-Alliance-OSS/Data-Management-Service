// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class MetadataModule(IOptions<IdentitySettings> identitySettings) : IEndpointModule
{
    /// <summary>
    /// Registers the OpenAPI specification endpoint with custom metadata and security scheme configuration.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/metadata/specifications",
            async context =>
            {
                var openApiJson = await context
                    .RequestServices.GetRequiredService<IHttpClientFactory>()
                    .CreateClient()
                    .GetStringAsync($"{context.Request.Scheme}://{context.Request.Host}/openapi/v1.json");

                var document = JsonNode.Parse(openApiJson)!.AsObject();

                // Update info section
                document["info"] = new JsonObject
                {
                    ["title"] = "Ed-Fi API Configuration Service API",
                    ["description"] = "API for configuring and managing the Ed-Fi API.",
                    ["contact"] = new JsonObject { ["url"] = "https://www.ed-fi.org/what-is-ed-fi/contact/" },
                    ["version"] = "1",
                };

                // Add components
                var components = document["components"]?.AsObject() ?? new JsonObject();
                document["components"] = components;

                // Add security schemes
                var securitySchemes = components["securitySchemes"]?.AsObject() ?? new JsonObject();
                components["securitySchemes"] = securitySchemes;

                var tokenUrl = $"{identitySettings.Value.Authority.TrimEnd('/')}/connect/token";

                securitySchemes["oauth2_client_credentials"] = new JsonObject
                {
                    ["type"] = "oauth2",
                    ["description"] = "Ed-Fi API OAuth 2.0 Client Credentials Grant Type authorization",
                    ["flows"] = new JsonObject
                    {
                        ["clientCredentials"] = new JsonObject
                        {
                            ["tokenUrl"] = tokenUrl,
                            ["scopes"] = new JsonObject(),
                        },
                    },
                };

                // Publish the Ed-Fi Problem Details schema and reference it from every reusable error
                // response, so generated client contracts match the application/problem+json bodies the
                // API actually returns.
                var schemas = components["schemas"]?.AsObject();
                if (schemas is null)
                {
                    schemas = new JsonObject();
                    components["schemas"] = schemas;
                }

                schemas["ProblemDetails"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["detail"] = new JsonObject { ["type"] = "string" },
                        ["type"] = new JsonObject { ["type"] = "string" },
                        ["title"] = new JsonObject { ["type"] = "string" },
                        ["status"] = new JsonObject { ["type"] = "integer", ["format"] = "int32" },
                        ["correlationId"] = new JsonObject { ["type"] = "string" },
                        ["validationErrors"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                        ["errors"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                };

                JsonObject ProblemDetailsResponse(string description) =>
                    new()
                    {
                        ["description"] = description,
                        ["content"] = new JsonObject
                        {
                            ["application/problem+json"] = new JsonObject
                            {
                                ["schema"] = new JsonObject
                                {
                                    ["$ref"] = "#/components/schemas/ProblemDetails",
                                },
                            },
                        },
                    };

                // Add reusable responses
                components["responses"] = new JsonObject
                {
                    ["BadRequest"] = ProblemDetailsResponse(
                        "Bad Request. The request was invalid and cannot be completed."
                    ),
                    ["Unauthorized"] = ProblemDetailsResponse(
                        "Unauthorized. The request requires authentication."
                    ),
                    ["Forbidden"] = ProblemDetailsResponse(
                        "Forbidden. The request is authenticated but not authorized to access this resource."
                    ),
                    ["NotFound"] = ProblemDetailsResponse("Not Found. The specified resource was not found."),
                    ["Conflict"] = ProblemDetailsResponse(
                        "Conflict. The request conflicts with the current state of the resource."
                    ),
                    ["InternalServerError"] = ProblemDetailsResponse(
                        "Internal Server Error. An error occurred while processing the request."
                    ),
                };

                // Add reusable parameters
                components["parameters"] = new JsonObject
                {
                    ["id"] = new JsonObject
                    {
                        ["name"] = "id",
                        ["in"] = "path",
                        ["required"] = true,
                        ["schema"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                        ["description"] = "Resource identifier",
                    },
                    ["offset"] = new JsonObject
                    {
                        ["name"] = "offset",
                        ["in"] = "query",
                        ["required"] = false,
                        ["schema"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["format"] = "int32",
                            ["minimum"] = 0,
                        },
                        ["description"] =
                            "Indicates how many items should be skipped before returning results.",
                    },
                    ["limit"] = new JsonObject
                    {
                        ["name"] = "limit",
                        ["in"] = "query",
                        ["required"] = false,
                        ["schema"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["format"] = "int32",
                            ["minimum"] = 1,
                        },
                        ["description"] =
                            "Indicates the maximum number of items that should be returned in the results.",
                    },
                    ["totalCount"] = new JsonObject
                    {
                        ["name"] = "totalCount",
                        ["in"] = "query",
                        ["required"] = false,
                        ["schema"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                        ["description"] =
                            "Indicates if the total number of items available should be returned in the 'Total-Count' header.",
                    },
                    ["orderBy"] = new JsonObject
                    {
                        ["name"] = "orderBy",
                        ["in"] = "query",
                        ["required"] = false,
                        ["schema"] = new JsonObject { ["type"] = "string" },
                        ["description"] =
                            "Name of the field to sort results by. Must be a valid field name for the resource.",
                    },
                    ["direction"] = new JsonObject
                    {
                        ["name"] = "direction",
                        ["in"] = "query",
                        ["required"] = false,
                        ["schema"] = new JsonObject { ["type"] = "string" },
                        ["description"] =
                            "Sort direction to use with orderBy. Accepted values are asc/ascending and desc/descending, case-insensitive. Defaults to 'asc' if omitted when orderBy is specified.",
                    },
                };

                // Add global security requirement
                document["security"] = new JsonArray
                {
                    new JsonObject { ["oauth2_client_credentials"] = new JsonArray() },
                };

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    document.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                );
            }
        );
    }
}
