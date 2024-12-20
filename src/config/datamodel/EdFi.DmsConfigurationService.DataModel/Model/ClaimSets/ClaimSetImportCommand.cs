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
        // IClaimSetDataProvider grants access to the repository to obtain actions and AuthStrategies
        public Validator(IClaimSetDataProvider claimSetDataProvider)
        {
            var resourceClaimValidator = new ResourceClaimValidator();
            IClaimSetDataProvider dataProvider = claimSetDataProvider;

            var dbActions = dataProvider.GetActions();
            var dbAuthStrategies = dataProvider.GetAuthorizationStrategies();

            RuleFor(c => c.Name)
                .NotEmpty()
                .WithMessage("ClaimSet Name is required.")
                .MaximumLength(256)
                .WithMessage("ClaimSet Name cannot exceed 256 characters.");

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

                        resourceClaimValidator.Validate(
                            dbActions,
                            dbAuthStrategies,
                            resourceClaim,
                            instance.ResourceClaims,
                            parentContext,
                            instance.Name
                        );
                    }
                );
        }
    }
}
