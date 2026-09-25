// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public sealed record ResourceClaimMetadataRow(int Id, string ClaimName);

public abstract record ResourceClaimMetadataResolveResult
{
    public sealed record Success(string ClaimName) : ResourceClaimMetadataResolveResult;

    public sealed record FailureResourceClaimNotFound() : ResourceClaimMetadataResolveResult;

    public sealed record FailureProjectionIntegrity(string FailureMessage)
        : ResourceClaimMetadataResolveResult;
}

public static class ResourceClaimMetadataResolver
{
    public static ResourceClaimMetadataResolveResult Resolve(
        int resourceClaimId,
        IReadOnlyList<Claim> claims,
        IReadOnlyList<ResourceClaimMetadataRow> metadata
    )
    {
        Dictionary<string, ResourceClaimMetadataRow> metadataByClaimName = metadata.ToDictionary(
            row => row.ClaimName,
            StringComparer.Ordinal
        );

        IReadOnlyList<string> claimNames = FlattenClaims(claims).Select(claim => claim.Name).ToList();

        foreach (string claimName in claimNames)
        {
            if (!metadataByClaimName.ContainsKey(claimName))
            {
                return new ResourceClaimMetadataResolveResult.FailureProjectionIntegrity(
                    $"Resource claim metadata is missing hierarchy claim '{claimName}'."
                );
            }
        }

        ResourceClaimMetadataRow? metadataRow = metadata.FirstOrDefault(row => row.Id == resourceClaimId);
        if (metadataRow is null)
        {
            return new ResourceClaimMetadataResolveResult.FailureResourceClaimNotFound();
        }

        return FlattenClaims(claims)
            .Any(claim => claim.Name.Equals(metadataRow.ClaimName, StringComparison.Ordinal))
            ? new ResourceClaimMetadataResolveResult.Success(metadataRow.ClaimName)
            : new ResourceClaimMetadataResolveResult.FailureResourceClaimNotFound();
    }

    private static IEnumerable<Claim> FlattenClaims(IEnumerable<Claim> claims)
    {
        Stack<Claim> pendingClaims = new(claims.Reverse());

        while (pendingClaims.TryPop(out Claim? claim))
        {
            yield return claim;

            foreach (Claim childClaim in claim.Claims.AsEnumerable().Reverse())
            {
                pendingClaims.Push(childClaim);
            }
        }
    }
}
