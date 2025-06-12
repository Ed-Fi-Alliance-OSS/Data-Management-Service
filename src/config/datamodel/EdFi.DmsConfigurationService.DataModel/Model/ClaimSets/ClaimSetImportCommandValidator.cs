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
                    if (
                        !context.RootContextData.TryGetValue("Actions", out var actionsAsObject)
                        || actionsAsObject is not List<string> actions
                        || !context.RootContextData.TryGetValue(
                            "AuthorizationStrategies",
                            out var authorizationStrategiesAsObject
                        )
                        || authorizationStrategiesAsObject is not List<string> authorizationStrategies
                        || !context.RootContextData.TryGetValue(
                            "ResourceClaimsHierarchyTuples",
                            out var resourceClaimsHierarchyTuplesAsObject
                        )
                        || resourceClaimsHierarchyTuplesAsObject
                            is not Dictionary<string, string?> resourceClaimsHierarchyTuples
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
                            resourceClaimValidator.Validate(
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
