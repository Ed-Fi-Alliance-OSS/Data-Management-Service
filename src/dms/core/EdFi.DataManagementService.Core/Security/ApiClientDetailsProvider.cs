// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security;

public interface IApiClientDetailsProvider
{
    ClientAuthorizations RetrieveApiClientDetailsFromToken(string jwtTokenHashCode, IList<Claim> claims);
}

public class ApiClientDetailsProvider() : IApiClientDetailsProvider
{
    public ClientAuthorizations RetrieveApiClientDetailsFromToken(string jwtTokenHashCode, IList<Claim> claims)
    {
        string[] requiredClaimTypes = ["scope", "jti", "namespacePrefixes"];

        var claimsDictionary = claims
            .Where(c => requiredClaimTypes.Contains(c.Type))
            .ToDictionary(c => c.Type, c => c.Value);
        string claimSetName = claimsDictionary.GetValueOrDefault("scope", string.Empty);
        string tokenId = GetTokenId(claimsDictionary, jwtTokenHashCode);
        string[] namespacePrefixes = GetNamespacePrefixes(claimsDictionary);
        ClientAuthorizations clientAuthorizations = new(
            tokenId,
            claimSetName,
            [],
            namespacePrefixes.Select(x => new NamespacePrefix(x)).ToList()
        );
        return clientAuthorizations;
    }

    private static string GetTokenId(Dictionary<string, string> claims, string jwtTokenHashCode)
    {
        return claims.GetValueOrDefault("jti", jwtTokenHashCode);
    }

    private static string[] GetNamespacePrefixes(Dictionary<string, string> claims)
    {
        string namespacePrefixesValue = claims.GetValueOrDefault("namespacePrefixes", string.Empty);
        return namespacePrefixesValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }
}
