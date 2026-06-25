// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;

public sealed record ResourceClaimMetadataSeed(string ResourceName, string ClaimName);

public interface IResourceClaimMetadataRepository
{
    Task<int> SeedResourceClaims(IReadOnlyList<ResourceClaimMetadataSeed> resourceClaims);
}

public sealed class NoOpResourceClaimMetadataRepository : IResourceClaimMetadataRepository
{
    public Task<int> SeedResourceClaims(IReadOnlyList<ResourceClaimMetadataSeed> resourceClaims) =>
        Task.FromResult(0);
}
