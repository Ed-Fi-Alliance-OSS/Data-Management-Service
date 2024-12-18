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
                .ForEach(resourceClaimRule =>
                {
                    resourceClaimRule.SetValidator(new ResourceClaimValidate());
                });
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
            .Must(BeValidChildren)
            .WithMessage("Invalid or circular reference in Children.");
    }

    private static bool BeValidChildren(List<ResourceClaim>? children)
    {
        if (children == null || !children.Any())
        {
            return true;
        }

        foreach (var child in children)
        {
            if (string.IsNullOrEmpty(child.Name) || child.Name.Length > 256)
            {
                return false;
            }

            if (child.Actions == null || !child.Actions.Any())
            {
                return false;
            }

            if (!BeValidChildren(child.Children))
            {
                return false;
            }
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
