// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Single source for the two namespace-authorization security-configuration (HTTP 500) messages.
/// Both the relational backend (when building the <c>Errors</c> array of a security-configuration
/// result) and Core's ProblemDetails formatter reference these so the wording stays identical
/// regardless of which layer produces the response.
/// </summary>
public static class NamespaceAuthorizationSecurityConfigurationMessages
{
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
}
