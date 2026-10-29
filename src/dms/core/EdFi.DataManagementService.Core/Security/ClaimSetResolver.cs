// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// The outcome of resolving the one claim set that applies to a request. Results carry no HTTP
/// behavior; each consumer maps them to its own response.
/// </summary>
internal abstract record ClaimSetResolution
{
    private ClaimSetResolution() { }

    /// <summary>
    /// Exactly one claim-set metadata record matched. <paramref name="ClaimSetName"/> is the
    /// selected name for request-local diagnostics and token info.
    /// </summary>
    public sealed record Success(ClaimSet ClaimSet, string ClaimSetName) : ClaimSetResolution;

    /// <summary>
    /// No scope value (or legacy name) matches any claim-set metadata record.
    /// </summary>
    public sealed record NoMatch : ClaimSetResolution;

    /// <summary>
    /// Scope values recognize more than one distinct claim-set record, so no claim set is chosen.
    /// </summary>
    public sealed record Ambiguous : ClaimSetResolution;

    /// <summary>
    /// Matching claim-set metadata contains duplicate names under the resolution comparison, so no
    /// record can be chosen safely.
    /// </summary>
    public sealed record InvalidMetadata : ClaimSetResolution;
}

/// <summary>
/// Pure resolution of the one claim set that applies to a request from the token's scope values
/// and an already-loaded claim-set metadata list. No metadata is fetched here.
/// </summary>
internal static class ClaimSetResolver
{
    private static readonly StringComparer NameComparer = StringComparer.InvariantCultureIgnoreCase;

    /// <summary>
    /// Matches scope values to claim-set records using the existing case-insensitive claim-set-name
    /// comparison. Nonempty <see cref="ClientAuthorizations.ScopeValues"/> is authoritative;
    /// otherwise a nonempty legacy <see cref="ClientAuthorizations.ClaimSetName"/> is the one
    /// literal candidate. Matching records are counted, not matching scope occurrences, and the
    /// order of scope values never chooses between records.
    /// </summary>
    public static ClaimSetResolution Resolve(
        ClientAuthorizations authorizations,
        IEnumerable<ClaimSet> claimSets
    )
    {
        IReadOnlyList<string> candidates = GetCandidates(authorizations);

        if (candidates.Count == 0)
        {
            return new ClaimSetResolution.NoMatch();
        }

        List<ClaimSet> matchingClaimSets =
        [
            .. claimSets.Where(claimSet =>
                candidates.Any(candidate => NameComparer.Equals(candidate, claimSet.Name))
            ),
        ];

        // Checked before ambiguity: two records with one name cannot be told apart, whatever else
        // the token recognizes.
        if (
            matchingClaimSets.Select(claimSet => claimSet.Name).Distinct(NameComparer).Count()
            != matchingClaimSets.Count
        )
        {
            return new ClaimSetResolution.InvalidMetadata();
        }

        switch (matchingClaimSets.Count)
        {
            case 0:
                return new ClaimSetResolution.NoMatch();
            case > 1:
                return new ClaimSetResolution.Ambiguous();
        }

        ClaimSet matched = matchingClaimSets[0];

        string[] matchingSpellings =
        [
            .. candidates
                .Where(candidate => NameComparer.Equals(candidate, matched.Name))
                .Distinct(StringComparer.Ordinal),
        ];

        // One spelling keeps the token's own spelling; several case variants use the metadata
        // spelling so the result does not depend on token order.
        string selectedName = matchingSpellings.Length == 1 ? matchingSpellings[0] : matched.Name;

        return new ClaimSetResolution.Success(matched, selectedName);
    }

    private static IReadOnlyList<string> GetCandidates(ClientAuthorizations authorizations)
    {
        if (authorizations.ScopeValues.Count > 0)
        {
            return authorizations.ScopeValues;
        }

        return authorizations.ClaimSetName.Length > 0 ? [authorizations.ClaimSetName] : [];
    }
}
