// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal static class QueryParameterNameAllocator
{
    public static IReadOnlyList<string> Allocate(
        IReadOnlyList<QueryParameterNameSeed> seeds,
        IReadOnlyList<string> reservedParameterNames
    )
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(reservedParameterNames);

        HashSet<string> usedNames = new(reservedParameterNames, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> nextSuffixByBaseName = new(StringComparer.OrdinalIgnoreCase);

        foreach (var reservedParameterName in usedNames)
        {
            nextSuffixByBaseName[reservedParameterName] = 2;
        }

        string[] resolvedNames = new string[seeds.Count];

        var orderedSeeds = seeds
            .OrderBy(static seed => seed.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static seed => seed.BaseName, StringComparer.Ordinal)
            .ThenBy(static seed => seed.QueryFieldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static seed => seed.QueryFieldName, StringComparer.Ordinal)
            .ThenBy(static seed => seed.Disambiguator, StringComparer.Ordinal)
            .ThenBy(static seed => seed.Index)
            .ToArray();

        foreach (var seed in orderedSeeds)
        {
            resolvedNames[seed.Index] = AllocateParameterName(seed.BaseName, usedNames, nextSuffixByBaseName);
        }

        return resolvedNames;
    }

    public static string CreateBaseName(string queryFieldName)
    {
        return PlanNamingConventions.SanitizeBareParameterName(
            PlanNamingConventions.CamelCaseFirstCharacter(queryFieldName)
        );
    }

    /// <summary>
    /// Collects the concrete SQL parameter names emitted by the namespace-prefix, claim-EdOrg, and
    /// ownership-token authorization parameterizations on <paramref name="authorization"/>. Query filter
    /// parameter allocation reserves these alongside the paging names so a query field whose sanitized name
    /// matches an authorization parameter is disambiguated with a suffix instead of colliding. An
    /// unreserved collision is rejected downstream as a duplicate filter parameter, surfacing as an
    /// UnknownFailure instead of a filtered result.
    /// </summary>
    /// <remarks>
    /// The ownership names are reserved from the parameterization's declared names rather than from what a
    /// statement binds. On PostgreSQL the array parameter is declared even for an empty token list, so its
    /// base name is reserved although the constant-false predicate never mentions it; reserving it only
    /// suffixes a colliding query field. On SQL Server an empty list declares no scalar names, so nothing is
    /// reserved and nothing needs to be: the SQL binds no ownership parameter either.
    /// </remarks>
    public static IReadOnlyList<string> CollectAuthorizationParameterNames(
        PageDocumentIdAuthorizationSpec? authorization
    )
    {
        if (authorization is null)
        {
            return [];
        }

        List<string> reservedNames = [];

        if (authorization.ClaimEducationOrganizationIdParameterization is { } claimParameterization)
        {
            reservedNames.AddRange(claimParameterization.ParameterNamesInOrder);
        }

        if (authorization.NamespacePrefixParameterization is { } namespacePrefixParameterization)
        {
            reservedNames.AddRange(namespacePrefixParameterization.ParameterNamesInOrder);
        }

        if (authorization.OwnershipTokenParameterization is { } ownershipTokenParameterization)
        {
            reservedNames.AddRange(ownershipTokenParameterization.ParameterNamesInOrder);
        }

        return reservedNames;
    }

    private static string AllocateParameterName(
        string baseName,
        ISet<string> usedNames,
        IDictionary<string, int> nextSuffixByBaseName
    )
    {
        if (usedNames.Add(baseName))
        {
            if (!nextSuffixByBaseName.TryGetValue(baseName, out var nextSuffix) || nextSuffix < 2)
            {
                nextSuffixByBaseName[baseName] = 2;
            }

            return baseName;
        }

        var suffix = nextSuffixByBaseName.TryGetValue(baseName, out var nextSuffixForBase)
            ? nextSuffixForBase
            : 2;
        var candidate = $"{baseName}_{suffix}";

        while (!usedNames.Add(candidate))
        {
            suffix++;
            candidate = $"{baseName}_{suffix}";
        }

        nextSuffixByBaseName[baseName] = suffix + 1;
        return candidate;
    }
}

internal sealed record QueryParameterNameSeed(
    int Index,
    string BaseName,
    string QueryFieldName,
    string Disambiguator
);
