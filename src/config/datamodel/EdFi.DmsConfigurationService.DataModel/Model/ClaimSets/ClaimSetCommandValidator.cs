// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetCommandValidator<T> : AbstractValidator<T>
    where T : IClaimSetCommand
{
    public ClaimSetCommandValidator(
        IClaimSetDataProvider claimSetDataProvider,
        bool isResourceClaimsOptional = false
    )
    {
        RuleFor(c => c.Name)
            .NotEmpty()
            .WithMessage("Please provide a valid claim set name.")
            .MaximumLength(256)
            .WithMessage("The claim set name must be less than 256 characters.");

        RuleFor(m => m.Name)
            .Matches(new Regex(ValidationConstants.ClaimSetNameNoWhiteSpaceRegex))
            .When(m => !string.IsNullOrEmpty(m.Name))
            .WithMessage(ValidationConstants.ClaimSetNameNoWhiteSpaceMessage);

        RuleFor(c => c.ResourceClaims)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .When(_ => !isResourceClaimsOptional)
            .WithMessage("Resource claims are required.");

        var resourceClaimValidator = new ResourceClaimValidator();
        List<string> dbActions = claimSetDataProvider.GetActions();
        List<string> dbAuthStrategies = claimSetDataProvider.GetAuthorizationStrategies();

        RuleForEach(c => c.ResourceClaims)
            .Custom(
                (resourceClaim, context) =>
                {
                    var parentContext = context;
                    var instance = parentContext.InstanceToValidate;

                    if (instance == null)
                    {
                        return;
                    }

                    var existingResourceClaims = instance.ResourceClaims ?? [];

                    resourceClaimValidator.Validate(
                        dbActions,
                        dbAuthStrategies,
                        resourceClaim,
                        existingResourceClaims,
                        parentContext,
                        instance.Name
                    );
                }
            )
            .When(c => c.ResourceClaims != null && c.ResourceClaims.Any());
    }
}
