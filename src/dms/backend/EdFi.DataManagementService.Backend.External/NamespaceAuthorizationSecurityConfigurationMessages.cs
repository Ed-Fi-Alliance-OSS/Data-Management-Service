// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Single source for the namespace-authorization security-configuration (HTTP 500) messages.
/// Both the relational backend (when building the <c>Errors</c> array of a security-configuration
/// result) and Core's ProblemDetails formatter reference these so the wording stays identical
/// regardless of which layer produces the response.
/// </summary>
public static class NamespaceAuthorizationSecurityConfigurationMessages
{
    /// <summary>
    /// A configured namespace prefix is null or empty, which cannot be parameterized into a <c>LIKE</c>
    /// predicate for <c>NamespaceBased</c> authorization. Fails closed as a security-configuration
    /// (HTTP 500) rather than escaping as a generic unknown failure from the parameterization factory.
    /// </summary>
    public const string InvalidNamespacePrefix =
        "The API client's NamespaceBased authorization is configured with a null or empty namespace prefix, which cannot be used to authorize access. Configure non-empty namespace prefixes.";

    /// <summary>
    /// The namespace authorization failure payload returned by the authorization provider (the AUTH1
    /// <c>ns1|index|kind</c> metadata) is invalid and cannot be mapped to the configured namespace
    /// authorization plan. Mirrors the relationship authorization invalid-payload diagnostic so a
    /// malformed namespace AUTH1 payload fails closed as a security-configuration (HTTP 500) rather than
    /// a generic unknown failure.
    /// </summary>
    public const string InvalidAuthorizationMetadata =
        "The namespace authorization failure payload returned by the authorization provider is invalid and cannot be mapped to the configured namespace authorization plan.";

    /// <summary>
    /// A resource is configured with <c>NamespaceBased</c> but no Namespace securable element resolves
    /// to a root-table column.
    /// </summary>
    public static string NoUsableRootColumn(string resourceName) =>
        $"The resource '{resourceName}' is configured with the 'NamespaceBased' authorization strategy, but no Namespace securable element resolves to a root table column. Collection-level Namespace paths are not eligible for namespace authorization.";

    /// <summary>
    /// The client has more namespace prefixes than SQL Server can bind as parameterized <c>LIKE</c>
    /// clauses for <c>NamespaceBased</c> authorization.
    /// </summary>
    public static string PrefixCapExceeded(int prefixCount) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "The API client has {0} namespace prefixes, which exceeds the SQL Server limit for NamespaceBased authorization. Configure fewer than 2,000 namespace prefixes.",
            prefixCount
        );

    /// <summary>
    /// The query's authorization parameters (namespace prefixes and/or authorization education organization
    /// ids) together with its filter and paging parameters exceed the number of parameters SQL Server can
    /// bind for a single command, even though each authorization list is within its own per-list limit.
    /// Applies to namespace-only, relationship-only, and composed query shapes.
    /// </summary>
    public static string CommandParameterCapExceeded(
        int namespacePrefixCount,
        int claimEducationOrganizationIdCount,
        int nonAuthorizationParameterCount
    ) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "The API client has {0} namespace prefixes and {1} authorization education organization ids, which together with {2} query and paging parameters exceed the SQL Server parameter limit for a single query. Configure fewer namespace prefixes, reduce the client's authorized education organizations, or use fewer query parameters.",
            namespacePrefixCount,
            claimEducationOrganizationIdCount,
            nonAuthorizationParameterCount
        );
}
