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
                    ["title"] = "Ed-Fi DMS Configuration Service API",
                    ["description"] =
                        "API for configuring and managing the Ed-Fi Data Management Service (DMS).",
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
                    ["description"] = "Ed-Fi DMS OAuth 2.0 Client Credentials Grant Type authorization",
                    ["flows"] = new JsonObject
                    {
                        ["clientCredentials"] = new JsonObject
                        {
                            ["tokenUrl"] = tokenUrl,
                            ["scopes"] = new JsonObject(),
                        },
                    },
                };

                // Add reusable responses
                components["responses"] = new JsonObject
                {
                    ["BadRequest"] = new JsonObject
                    {
                        ["description"] = "Bad Request. The request was invalid and cannot be completed.",
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject
                            {
                                ["schema"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["title"] = new JsonObject { ["type"] = "string" },
                                        ["errors"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["additionalProperties"] = new JsonObject
                                            {
                                                ["type"] = "array",
                                                ["items"] = new JsonObject { ["type"] = "string" },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                    ["Unauthorized"] = new JsonObject
                    {
                        ["description"] = "Unauthorized. The request requires authentication.",
                    },
                    ["Forbidden"] = new JsonObject
                    {
                        ["description"] =
                            "Forbidden. The request is authenticated but not authorized to access this resource.",
                    },
                    ["NotFound"] = new JsonObject
                    {
                        ["description"] = "Not Found. The specified resource was not found.",
                    },
                    ["Conflict"] = new JsonObject
                    {
                        ["description"] =
                            "Conflict. The request conflicts with the current state of the resource.",
                    },
                    ["InternalServerError"] = new JsonObject
                    {
                        ["description"] =
                            "Internal Server Error. An error occurred while processing the request.",
                    },
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
                            ["default"] = 0,
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
                            ["maximum"] = 100,
                            ["default"] = 25,
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
