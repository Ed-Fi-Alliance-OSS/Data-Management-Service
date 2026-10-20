// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Describes client-credentials sign-in in the generated /openapi/v1.json document.
/// </summary>
public static class ConfigurationServiceOpenApiTransformers
{
    private const string EdFiWebsite = "https://www.ed-fi.org/";

    public const string SecuritySchemeName = "oauth2_client_credentials";

    /// <summary>
    /// Sets the document info, declares the client-credentials security scheme and requires it at
    /// the root. The token URL is the request base the document is served for (scheme, host and
    /// PathBase, after forwarded headers when enabled) followed by /connect/token. It never reads
    /// the identity provider authority, so a document fetched through a proxy points at the proxy.
    /// </summary>
    public static Task TransformDocument(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        document.Info = new OpenApiInfo
        {
            Title = "Ed-Fi API Configuration Service API",
            Description = "API for configuring and managing the Ed-Fi API.",
            Contact = new OpenApiContact { Url = new Uri(new Uri(EdFiWebsite), "what-is-ed-fi/contact/") },
            Version = "1",
        };

        var tokenUrl = $"{RequestBase(document)}/connect/token";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SecuritySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "Ed-Fi API OAuth 2.0 Client Credentials Grant Type authorization",
            Flows = new OpenApiOAuthFlows
            {
                ClientCredentials = new OpenApiOAuthFlow
                {
                    TokenUrl = new Uri(tokenUrl, UriKind.RelativeOrAbsolute),
                    Scopes = AuthorizationScopes.AllScopes.ToDictionary(s => s.Name, s => s.Description),
                },
            },
        };

        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeName, document)] = [],
            },
        ];

        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks every operation that needs no token with an empty security requirement, which
    /// overrides the root requirement. Operations that carry authorization metadata keep a null
    /// Security and inherit the root requirement.
    /// </summary>
    public static Task TransformOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (!metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [];
        }

        return Task.CompletedTask;
    }

    // ASP.NET fills servers[0] from the current request, so reusing it keeps the token URL equal
    // to the document's own server URL.
    private static string RequestBase(OpenApiDocument document) =>
        (document.Servers?.FirstOrDefault()?.Url ?? string.Empty).TrimEnd('/');
}
