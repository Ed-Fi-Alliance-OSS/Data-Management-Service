// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// ReSharper disable ClassNeverInstantiated.Global

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IResourceClaimRepository
{
    Task<ResourceClaimListResult> GetResourceClaims(ResourceClaimQuery query);
    Task<ResourceClaimGetResult> GetResourceClaim(long id);
    Task<ResourceClaimActionListResult> GetResourceClaimActions(ResourceClaimActionQuery query);
    Task<ResourceClaimActionAuthStrategyListResult> GetResourceClaimActionAuthStrategies(
        ResourceClaimActionAuthStrategyQuery query
    );
}

public abstract record ResourceClaimListResult
{
    public record Success(IReadOnlyList<ResourceClaimResponse> ResourceClaims) : ResourceClaimListResult;

    public record FailureHierarchyNotFound() : ResourceClaimListResult;

    public record FailureProjectionIntegrity(string FailureMessage) : ResourceClaimListResult;

    public record FailureUnknown(string FailureMessage) : ResourceClaimListResult;
}

public abstract record ResourceClaimGetResult
{
    public record Success(ResourceClaimResponse ResourceClaim) : ResourceClaimGetResult;

    public record FailureNotFound() : ResourceClaimGetResult;

    public record FailureHierarchyNotFound() : ResourceClaimGetResult;

    public record FailureProjectionIntegrity(string FailureMessage) : ResourceClaimGetResult;

    public record FailureUnknown(string FailureMessage) : ResourceClaimGetResult;
}

public abstract record ResourceClaimActionListResult
{
    public record Success(IReadOnlyList<ResourceClaimActionResponse> ResourceClaimActions)
        : ResourceClaimActionListResult;

    public record FailureHierarchyNotFound() : ResourceClaimActionListResult;

    public record FailureProjectionIntegrity(string FailureMessage) : ResourceClaimActionListResult;

    public record FailureUnknown(string FailureMessage) : ResourceClaimActionListResult;
}

public abstract record ResourceClaimActionAuthStrategyListResult
{
    public record Success(
        IReadOnlyList<ResourceClaimActionAuthStrategyResponse> ResourceClaimActionAuthStrategies
    ) : ResourceClaimActionAuthStrategyListResult;

    public record FailureHierarchyNotFound() : ResourceClaimActionAuthStrategyListResult;

    public record FailureProjectionIntegrity(string FailureMessage)
        : ResourceClaimActionAuthStrategyListResult;

    public record FailureUnknown(string FailureMessage) : ResourceClaimActionAuthStrategyListResult;
}
