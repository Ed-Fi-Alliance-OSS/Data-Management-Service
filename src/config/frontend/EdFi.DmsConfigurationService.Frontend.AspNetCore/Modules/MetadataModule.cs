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
                    // Every Ed-Fi error response carries all seven members (validationErrors {} and
                    // errors [] when empty), so each is required in the published schema.
                    ["required"] = new JsonArray
                    {
                        "type",
                        "title",
                        "detail",
                        "status",
                        "correlationId",
                        "validationErrors",
                        "errors",
                    },
                };

                // OAuth 2.0 protocol errors (RFC 6749 section 5.2) are application/json
                // { error, error_description } bodies, not Ed-Fi Problem Details; the token, introspection,
                // and revocation operations reference this schema instead.
                schemas["OAuthError"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["error"] = new JsonObject { ["type"] = "string" },
                        ["error_description"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray { "error" },
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
                    // A wrong-method request produces a runtime 405 with the Ed-Fi Problem Details
                    // urn:ed-fi:api:method-not-allowed body, so the reusable response exists in the
                    // catalog. It is intentionally not referenced from any operation: a method mismatch
                    // has no defined OpenAPI operation to attach it to, and the exact per-operation error
                    // documentation is owned by DMS-1293.
                    ["MethodNotAllowed"] = ProblemDetailsResponse(
                        "Method Not Allowed. The request method is not supported for this resource."
                    ),
                    ["Conflict"] = ProblemDetailsResponse(
                        "Conflict. The request conflicts with the current state of the resource."
                    ),
                    ["InternalServerError"] = ProblemDetailsResponse(
                        "Internal Server Error. An error occurred while processing the request."
                    ),
                    ["UnsupportedMediaType"] = ProblemDetailsResponse(
                        "Unsupported Media Type. The request content type is not supported."
                    ),
                    ["BadGateway"] = ProblemDetailsResponse(
                        "Bad Gateway. An upstream identity provider returned an error."
                    ),
                    ["OAuthError"] = new JsonObject
                    {
                        ["description"] = "OAuth 2.0 protocol error (RFC 6749 section 5.2).",
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject
                            {
                                ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/OAuthError" },
                            },
                        },
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

                // Reference the reusable error responses from each applicable operation so the generated
                // contract documents the application/problem+json bodies the API returns. OAuth protocol
                // endpoints instead document their application/json { error, error_description } shape and
                // are never described as Ed-Fi Problem Details endpoints.
                AttachErrorResponses(document);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    document.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                );
            }
        );
    }

    // Walks every path and operation and references the reusable error responses that apply, using a
    // method and route heuristic because handlers return untyped IResult and per-operation error sets
    // cannot be inferred from types. OAuth protocol endpoints instead reference the OAuth error response,
    // while the connect-register endpoint and the v3 resources reference the Ed-Fi Problem Details
    // responses. Only error statuses are written, so success response metadata is left intact.
    private static void AttachErrorResponses(JsonObject document)
    {
        if (document["paths"] is not JsonObject paths)
        {
            return;
        }

        foreach ((string pathKey, JsonNode? pathNode) in paths)
        {
            if (pathNode is not JsonObject pathItem)
            {
                continue;
            }

            bool isOAuthProtocol =
                pathKey.Contains("/connect/token", StringComparison.OrdinalIgnoreCase)
                || pathKey.Contains("/connect/introspect", StringComparison.OrdinalIgnoreCase)
                || pathKey.Contains("/connect/revoke", StringComparison.OrdinalIgnoreCase);

            bool isEdFiResource =
                pathKey.StartsWith("/v3/", StringComparison.OrdinalIgnoreCase)
                || pathKey.StartsWith("/management/", StringComparison.OrdinalIgnoreCase)
                || pathKey.Contains("/connect/register", StringComparison.OrdinalIgnoreCase);

            if (!isOAuthProtocol && !isEdFiResource)
            {
                continue;
            }

            bool hasPathParameter = pathKey.Contains('{');

            foreach ((string methodName, JsonNode? operationNode) in pathItem)
            {
                string method = methodName.ToLowerInvariant();
                if (
                    method is not ("get" or "put" or "post" or "delete" or "patch" or "options" or "head")
                    || operationNode is not JsonObject operation
                )
                {
                    continue;
                }

                JsonObject? responses = operation["responses"]?.AsObject();
                if (responses is null)
                {
                    responses = new JsonObject();
                    operation["responses"] = responses;
                }

                if (isOAuthProtocol)
                {
                    AddOAuthErrorResponses(responses, pathKey);
                }
                else
                {
                    AddEdFiErrorResponses(responses, pathKey, method, hasPathParameter);
                }
            }
        }
    }

    private static void AddEdFiErrorResponses(
        JsonObject responses,
        string pathKey,
        string method,
        bool hasPathParameter
    )
    {
        void Reference(string status, string component) =>
            responses[status] = new JsonObject { ["$ref"] = $"#/components/responses/{component}" };

        // Authentication, authorization, and unexpected-server failures apply to every operation.
        Reference("401", "Unauthorized");
        Reference("403", "Forbidden");
        Reference("500", "InternalServerError");

        // A malformed request can produce a 400 on any read (e.g. paging/query validation on a
        // collection GET) or write; write operations can additionally reject the media type (415).
        if (method is "get" or "post" or "put" or "patch")
        {
            Reference("400", "BadRequest");
        }
        if (method is "post" or "put" or "patch")
        {
            Reference("415", "UnsupportedMediaType");
        }

        if (hasPathParameter)
        {
            Reference("404", "NotFound");
        }

        if (method is "post" or "put")
        {
            Reference("409", "Conflict");
        }

        // Operations that provision identity-provider state (registration, vendors, API clients) can
        // fail upstream and return 502.
        bool provisionsIdentityProvider =
            pathKey.Contains("/connect/register", StringComparison.OrdinalIgnoreCase)
            || pathKey.Contains("/v3/vendors", StringComparison.OrdinalIgnoreCase)
            || pathKey.Contains("/v3/apiClients", StringComparison.OrdinalIgnoreCase);
        if (provisionsIdentityProvider && method is not "get")
        {
            Reference("502", "BadGateway");
        }
    }

    private static void AddOAuthErrorResponses(JsonObject responses, string pathKey)
    {
        void Reference(string status) =>
            responses[status] = new JsonObject { ["$ref"] = "#/components/responses/OAuthError" };

        // A missing or malformed request is invalid_request 400 on every token-family endpoint. The token
        // endpoint additionally reports invalid_client 401 and temporarily_unavailable 503.
        Reference("400");
        if (pathKey.Contains("/connect/token", StringComparison.OrdinalIgnoreCase))
        {
            Reference("401");
            Reference("503");
        }
    }
}
