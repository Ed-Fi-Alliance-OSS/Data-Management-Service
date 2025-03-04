// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ResourceClaimValidator
{
    private readonly List<string> _duplicateResources = [];

    public void Validate<T>(
        List<string> dbActions,
        List<string> dbAuthStrategies,
        ResourceClaim resourceClaim,
        List<ResourceClaim> existingResourceClaims,
        ValidationContext<T> context,
        string? claimSetName
    )
    {
        context.MessageFormatter.AppendArgument("Name", claimSetName);
        context.MessageFormatter.AppendArgument("ResourceClaimName", resourceClaim.Name);

        const string PropertyName = "ResourceClaims";
        ValidateDuplicateResourceClaim(resourceClaim, existingResourceClaims, context, PropertyName);

        ValidateCRUD(resourceClaim.Actions, dbActions, context, PropertyName);

        ValidateAuthStrategies(dbAuthStrategies, resourceClaim, context, PropertyName);
        ValidateAuthStrategiesOverride(dbAuthStrategies, resourceClaim, context, PropertyName);
        ValidateChildren(dbActions, dbAuthStrategies, resourceClaim, context, claimSetName);
    }

    private void ValidateDuplicateResourceClaim<T>(
        ResourceClaim resourceClaim,
        List<ResourceClaim> existingResourceClaims,
        ValidationContext<T> context,
        string propertyName
    )
    {
        if (existingResourceClaims.Count(x => x.Name == resourceClaim.Name) > 1)
        {
            if (
                _duplicateResources == null
                || resourceClaim.Name == null
                || _duplicateResources.Contains(resourceClaim.Name)
            )
            {
                return;
            }
            _duplicateResources.Add(resourceClaim.Name);
            context.AddFailure(
                propertyName,
                "Only unique resource claims can be added. The following is a duplicate resource: '{ResourceClaimName}'."
            );
        }
    }

    private void ValidateChildren<T>(
        List<string> dbActions,
        List<string> dbAuthStrategies,
        ResourceClaim resourceClaim,
        ValidationContext<T> context,
        string? claimSetName
    )
    {
        if (resourceClaim.Children.Count != 0)
        {
            foreach (var child in resourceClaim.Children)
            {
                Validate(dbActions, dbAuthStrategies, child, resourceClaim.Children, context, claimSetName);
            }
        }
    }

    private static void ValidateAuthStrategiesOverride<T>(
        List<string> dbAuthStrategies,
        ResourceClaim resourceClaim,
        ValidationContext<T> context,
        string propertyName
    )
    {
        if (resourceClaim.AuthorizationStrategyOverridesForCRUD.Count != 0)
        {
            foreach (
                var authStrategyOverrideWithAction in resourceClaim.AuthorizationStrategyOverridesForCRUD
            )
            {
                if (authStrategyOverrideWithAction?.AuthorizationStrategies != null)
                {
                    foreach (
                        var authStrategyOverride in authStrategyOverrideWithAction.AuthorizationStrategies
                    )
                    {
                        if (
                            authStrategyOverride?.AuthorizationStrategyName != null
                            && !dbAuthStrategies.Contains(authStrategyOverride.AuthorizationStrategyName)
                        )
                        {
                            context.MessageFormatter.AppendArgument(
                                "AuthorizationStrategyName",
                                authStrategyOverride.AuthorizationStrategyName
                            );
                            context.AddFailure(
                                propertyName,
                                "This resource claim contains an authorization strategy which is not in the system. ClaimSet Name: '{Name}' Resource name: '{ResourceClaimName}' Authorization strategy: '{AuthorizationStrategyName}'."
                            );
                        }
                    }
                }
            }
        }
    }

    private static void ValidateAuthStrategies<T>(
        List<string> dbAuthStrategies,
        ResourceClaim resourceClaim,
        ValidationContext<T> context,
        string propertyName
    )
    {
        if (resourceClaim.DefaultAuthorizationStrategiesForCRUD.Count != 0)
        {
            foreach (var defaultASWithAction in resourceClaim.DefaultAuthorizationStrategiesForCRUD)
            {
                if (defaultASWithAction?.AuthorizationStrategies == null)
                {
                    continue;
                }

                foreach (var defaultAs in defaultASWithAction.AuthorizationStrategies)
                {
                    if (
                        defaultAs.AuthorizationStrategyName != null
                        && !dbAuthStrategies.Contains(defaultAs.AuthorizationStrategyName)
                    )
                    {
                        context.MessageFormatter.AppendArgument(
                            "AuthorizationStrategyName",
                            defaultAs.AuthorizationStrategyName
                        );
                        context.AddFailure(
                            propertyName,
                            "This resource claim contains an authorization strategy which is not in the system. ClaimSet Name: '{Name}' Resource name: '{ResourceClaimName}' Authorization strategy: '{AuthorizationStrategyName}'."
                        );
                    }
                }
            }
        }
    }

    private static void ValidateCRUD<T>(
        List<ResourceClaimAction>? resourceClaimActions,
        List<string> dbActions,
        ValidationContext<T> context,
        string propertyName
    )
    {
        if (resourceClaimActions != null && resourceClaimActions.Count != 0)
        {
            if (!resourceClaimActions.Exists(x => x.Enabled))
            {
                context.AddFailure(
                    propertyName,
                    "A resource must have at least one action associated with it to be added. Resource name: '{ResourceClaimName}'"
                );
            }
            else
            {
                var duplicates = resourceClaimActions
                    .GroupBy(x => x.Name)
                    .Where(g => g.Count() > 1)
                    .Select(y => y.Key)
                    .ToList();
                foreach (string? duplicate in duplicates)
                {
                    context.AddFailure(
                        propertyName,
                        $"{duplicate} action is duplicated. Resource name: '{{ResourceClaimName}}'"
                    );
                }
                foreach (var action in resourceClaimActions.Select(x => x.Name))
                {
                    if (
                        !dbActions.Exists(actionName =>
                            actionName.Equals(action, StringComparison.InvariantCultureIgnoreCase)
                        )
                    )
                    {
                        context.AddFailure(
                            propertyName,
                            $"{action} is not a valid action. Resource name: '{{ResourceClaimName}}'"
                        );
                    }
                }
            }
        }
        else
        {
            context.AddFailure(
                propertyName,
                $"Actions can not be empty. Resource name: '{{ResourceClaimName}}'"
            );
        }
    }
}
