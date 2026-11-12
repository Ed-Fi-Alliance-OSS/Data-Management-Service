// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class MetadataModule : IEndpointModule
{
    /// <summary>
    /// Registers the OpenAPI specification endpoint with the reusable components added to the generated document.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/metadata/specifications",
            async context =>
            {
                // PathBase is required: the OpenAPI document is served relative to it, so omitting
                // it requests a path that does not exist on any deployment that sets
                // AppSettings:PathBase, and the 404 surfaces to the caller as a 500.
                var openApiJson = await context
                    .RequestServices.GetRequiredService<IHttpClientFactory>()
                    .CreateClient()
                    .GetStringAsync(
                        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/openapi/v1.json"
                    );

                var document = JsonNode.Parse(openApiJson)!.AsObject();

                // Add components
                var components = document["components"]?.AsObject() ?? new JsonObject();
                document["components"] = components;

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
                        ["schema"] = new JsonObject { ["type"] = "integer", ["format"] = "int32" },
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

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    document.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                );
            }
        );
    }
}
