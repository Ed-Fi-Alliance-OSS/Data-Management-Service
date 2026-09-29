// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Identity;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Loads the fixed identity OpenAPI document once from the embedded resource
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
    /// Reads the identity contract version from the <c>IdentityContractVersion</c>
    /// <see cref="AssemblyMetadataAttribute" /> on <see cref="IIdentityService" />'s assembly. The
    /// Identity project stamps it from its own <c>VersionPrefix</c>, which a global
    /// <c>/p:Version</c> or <c>/p:InformationalVersion</c> does not override, so the stamp is the
    /// contract's version rather than whatever DMS release the host was built as.
    /// </summary>
    private static string ResolveContractVersion() =>
        typeof(IIdentityService)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "IdentityContractVersion")
            ?.Value
        ?? throw new InvalidOperationException(
            "The identity contract assembly carries no IdentityContractVersion metadata."
        );
}
