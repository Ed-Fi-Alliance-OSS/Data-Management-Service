// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetImportCommand
{
    public required string Name { get; set; }
    public required List<ResourceClaim> ResourceClaims { get; set; }

    public class Validator : AbstractValidator<ClaimSetImportCommand>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(256);

            RuleFor(c => c.ResourceClaims)
                .NotEmpty()
                .WithMessage("At least one ResourceClaim is required.")
                .Must(BeUniqueNames)
                .WithMessage("ResourceClaim names must be unique.")
                .ForEach(resourceClaimRule =>
                {
                    resourceClaimRule.SetValidator(new ResourceClaimValidate());
                });
        }

        private static bool BeUniqueNames(List<ResourceClaim>? resourceClaims)
        {
            if (resourceClaims == null || !resourceClaims.Any())
            {
                return true;
            }

            return resourceClaims.Select(rc => rc.Name).Distinct().Count() == resourceClaims.Count;
        }
    }
}

public class ResourceClaimValidate : AbstractValidator<ResourceClaim>
{
    public ResourceClaimValidate()
    {
        RuleFor(rc => rc.Name).NotEmpty().MaximumLength(256);

        RuleFor(rc => rc.ParentName).MaximumLength(256);

        RuleFor(rc => rc.Actions)
            .NotEmpty()
            .WithMessage("Actions are required.")
            .ForEach(actionRule =>
            {
                actionRule.SetValidator(new ResourceClaimActionValidator());
            });

        RuleFor(rc => rc.Children)
            .Must(children => BeValidChildren(children))
            .WithMessage("Invalid or circular reference in Children.");
    }

    private static bool BeValidChildren(
        IEnumerable<ResourceClaim>? children,
        HashSet<string>? visitedNames = null
    )
    {
        visitedNames ??= new HashSet<string>();

        if (children == null)
        {
            return true;
        }

        foreach (var child in children)
        {
            if (!visitedNames.Add(child.Name!))
            {
                return false;
            }

            if (!BeValidChildren(child.Children, visitedNames))
            {
                return false;
            }

            visitedNames.Remove(child.Name!);
        }

        return true;
    }
}

public class ResourceClaimActionValidator : AbstractValidator<ResourceClaimAction>
{
    public ResourceClaimActionValidator()
    {
        RuleFor(action => action.Name).NotEmpty().MaximumLength(128);

        RuleFor(action => action.Enabled).NotNull();
    }
}
