// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides authorization filters for namespace based authorization strategy
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class NamespaceBasedFiltersProvider : IAuthorizationFiltersProvider
{
    private const string AuthorizationStrategyName = "NamespaceBased";

    public AuthorizationStrategyEvaluator GetFilters(ClientAuthorizations authorizations)
    {
        var filters = new List<AuthorizationFilter>();
        foreach (var namespacePrefix in authorizations.NamespacePrefixes)
        {
            filters.Add(
                new AuthorizationFilter(
                    "Namespace",
                    namespacePrefix.Value,
                    "Access to the resource item could not be authorized based on the caller's NamespacePrefix claims: {claims}.",
                    FilterComparison.StartsWith
                )
            );
        }

        return new AuthorizationStrategyEvaluator([.. filters], FilterOperator.Or);
    }
}
