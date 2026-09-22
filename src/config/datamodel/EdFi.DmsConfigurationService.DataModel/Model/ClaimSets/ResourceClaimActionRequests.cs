// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public interface IResourceClaimActionsOnClaimSetRequest
{
    int ClaimSetId { get; set; }
    int ResourceClaimId { get; set; }
    List<ResourceClaimActionRequest> ResourceClaimActions { get; set; }
}

public sealed class AddResourceClaimActionsOnClaimSetRequest : IResourceClaimActionsOnClaimSetRequest
{
    public required int ClaimSetId { get; set; }
    public required int ResourceClaimId { get; set; }
    public required List<ResourceClaimActionRequest> ResourceClaimActions { get; set; } = [];

    public sealed class Validator
        : ResourceClaimActionsOnClaimSetRequestValidator<AddResourceClaimActionsOnClaimSetRequest>;
}

public sealed class EditResourceClaimActionsOnClaimSetRequest : IResourceClaimActionsOnClaimSetRequest
{
    public required int ClaimSetId { get; set; }
    public required int ResourceClaimId { get; set; }
    public required List<ResourceClaimActionRequest> ResourceClaimActions { get; set; } = [];

    public sealed class Validator
        : ResourceClaimActionsOnClaimSetRequestValidator<EditResourceClaimActionsOnClaimSetRequest>;
}

public sealed class ResourceClaimActionRequest
{
    public string? Name { get; set; }
    public bool Enabled { get; set; }
}

public sealed class OverrideAuthStategyOnClaimSetRequest
{
    public required int ClaimSetId { get; set; }
    public required int ResourceClaimId { get; set; }
    public required string ActionName { get; set; } = string.Empty;
    public List<int>? AuthStrategyIds { get; set; } = [];
    public required List<string> AuthorizationStrategies { get; set; } = [];

    public sealed class Validator : AbstractValidator<OverrideAuthStategyOnClaimSetRequest>
    {
        public Validator()
        {
            RuleFor(request => request.ClaimSetId).NotEmpty();
            RuleFor(request => request.ResourceClaimId).NotEmpty();
            RuleFor(request => request.ActionName).NotEmpty();
            RuleFor(request => request.AuthorizationStrategies).NotNull().NotEmpty();
            RuleForEach(request => request.AuthorizationStrategies).NotNull();
        }
    }
}

public abstract class ResourceClaimActionsOnClaimSetRequestValidator<T> : AbstractValidator<T>
    where T : IResourceClaimActionsOnClaimSetRequest
{
    protected ResourceClaimActionsOnClaimSetRequestValidator()
    {
        RuleFor(request => request.ClaimSetId).NotEmpty();
        RuleFor(request => request.ResourceClaimId).NotEmpty();
        RuleFor(request => request.ResourceClaimActions).NotNull().NotEmpty();
        RuleForEach(request => request.ResourceClaimActions).NotNull();
        RuleFor(request => request.ResourceClaimActions)
            .Must(actions => actions is not null && actions.Exists(action => action is { Enabled: true }))
            .WithMessage("At least one resource claim action must be enabled.");
        RuleFor(request => request.ResourceClaimActions)
            .Must(actions =>
                actions is null
                || actions
                    .Where(action => action is not null && !string.IsNullOrWhiteSpace(action.Name))
                    .Select(action => action.Name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == actions.Count(action => action is not null && !string.IsNullOrWhiteSpace(action.Name))
            )
            .WithMessage("Resource claim action names must not be duplicated.");
    }
}
