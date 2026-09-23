// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Identity;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Loads the fixed identity OpenAPI document (design.md D2) once from the embedded resource
/// <c>OpenApi/identity-v2-openapi.json</c> and stamps it with <c>x-edfi-identity-contract-version</c>,
/// read from the identity contract assembly's <see cref="AssemblyInformationalVersionAttribute" /> and
/// stripped of any trailing <c>+commit</c> suffix. The loader owns the single process-wide
/// <see cref="Lazy{T}" />, so <see cref="ApiService.GetIdentityOpenApiSpecification" /> only clones and
/// decorates the cached value rather than caching it again per instance.
/// </summary>
internal static class IdentityOpenApiDocument
{
    private const string EmbeddedResourceName =
        "EdFi.DataManagementService.Core.OpenApi.identity-v2-openapi.json";

    private static readonly Lazy<JsonNode> _document = new(Load);

    /// <summary>
    /// The loaded identity OpenAPI document, stamped with <c>x-edfi-identity-contract-version</c>.
    /// Callers must clone before mutating - this instance is shared across every request.
    /// </summary>
    public static JsonNode Document => _document.Value;

    private static JsonNode Load()
    {
        Assembly assembly = typeof(IdentityOpenApiDocument).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Could not load embedded identity OpenAPI document '{EmbeddedResourceName}'."
            );
        }

        using StreamReader reader = new(stream);
        string json = reader.ReadToEnd();

        JsonNode document =
            JsonNode.Parse(json)
            ?? throw new InvalidOperationException(
                "The embedded identity OpenAPI document is not valid JSON."
            );

        document["x-edfi-identity-contract-version"] = ResolveContractVersion();

        return document;
    }

    /// <summary>
    /// Reads the identity contract's informational version from <see cref="IIdentityService" />'s
    /// assembly, stripping any trailing <c>+commit</c> metadata suffix so the stamp is a bare
    /// semantic version.
    /// </summary>
    private static string ResolveContractVersion()
    {
        string informationalVersion =
            typeof(IIdentityService)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "0.0.0";

        int plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex < 0 ? informationalVersion : informationalVersion[..plusIndex];
    }
}
