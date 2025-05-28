// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetImportCommandValidator : ClaimSetCommandValidator<ClaimSetImportCommand>
{
    public ClaimSetImportCommandValidator()
    {
        RuleFor(m => m)
            .Custom(
                (claimSet, context) =>
                {
                    // Retrieve dynamic values from the validation context
                    var actions = context.RootContextData["Actions"] as List<string>;
                    var authorizationStrategies =
                        context.RootContextData["AuthorizationStrategies"] as List<string>;
                    var resourceClaimsHierarchyTuples =
                        context.RootContextData["ResourceClaimsHierarchyTuples"]
                        as Dictionary<string, string?>;

                    if (
                        actions == null
                        || authorizationStrategies == null
                        || resourceClaimsHierarchyTuples == null
                    )
                    {
                        context.AddFailure("Validation context is missing required data for validation.");
                        return;
                    }

                    var resourceClaimValidator = new ResourceClaimValidator();

                    if (claimSet.ResourceClaims != null && claimSet.ResourceClaims.Count != 0)
                    {
                        foreach (var resourceClaim in claimSet.ResourceClaims)
                        {
                            resourceClaimValidator.Validate<ClaimSetImportCommand>(
                                actions,
                                authorizationStrategies,
                                resourceClaim,
                                resourceClaimsHierarchyTuples,
                                context,
                                claimSet.Name
                            );
                        }
                    }
                }
            );
    }
}
