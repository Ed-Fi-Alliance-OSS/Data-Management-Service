// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ResourceClaimValidator
{
    public void Validate<T>(
        List<string> actionNames,
        List<string> authorizationStrategyNames,
        ResourceClaim resourceClaim,
        Dictionary<string, string?> parentClaimByResourceClaim,
        ValidationContext<T> context,
        string? claimSetName
    )
    {
        const string PropertyName = "ResourceClaims";

        context.MessageFormatter.AppendArgument("Name", claimSetName);
        context.MessageFormatter.AppendArgument("ResourceClaimName", resourceClaim.Name);

        ValidateResourceClaimExists(context, resourceClaim, parentClaimByResourceClaim, PropertyName);

        ValidateNoDuplicateResourceClaims(resourceClaim, context, PropertyName);
        ValidateActions(resourceClaim.Actions, actionNames, context, PropertyName);
        ValidateAuthStrategies(authorizationStrategyNames, resourceClaim, context, PropertyName);
        ValidateAuthStrategiesOverride(authorizationStrategyNames, resourceClaim, context, PropertyName);

        ValidateChildren(
            actionNames,
            authorizationStrategyNames,
            resourceClaim,
            parentClaimByResourceClaim,
            context,
            claimSetName,
            PropertyName
        );
    }

    private void ValidateResourceClaimExists<T>(
        ValidationContext<T> context,
        ResourceClaim resourceClaim,
        IDictionary<string, string?> parentClaimByResourceClaim,
        string propertyName
    )
    {
        if (!parentClaimByResourceClaim.ContainsKey(resourceClaim.Name!))
        {
            context.AddFailure(
                propertyName,
                "This Claim Set contains a resource which is not in the system. ClaimSet Name: '{Name}' Resource name: '{ResourceClaimName}'"
            );
        }
    }

    private void ValidateNoDuplicateResourceClaims<T>(
        ResourceClaim resourceClaim,
        ValidationContext<T> context,
        string propertyName
    )
    {
        // Use RootContextData to track seen resources and duplicates
        var rootData = context.RootContextData;

        if (
            !rootData.TryGetValue("SeenResourceClaims", out var seenObject)
            || seenObject is not HashSet<string> seenResources
        )
        {
            seenResources = [];
            rootData["SeenResourceClaims"] = seenResources;
        }

        if (
            !rootData.TryGetValue("DuplicateResources", out var duplicatesObject)
            || duplicatesObject is not HashSet<string> duplicateResources
        )
        {
            duplicateResources = [];
            rootData["DuplicateResources"] = duplicateResources;
        }

        string? resourceKey = resourceClaim.Name;

        if (!string.IsNullOrWhiteSpace(resourceKey))
        {
            // Has this resource claim already been seen (i.e. it's a duplicate)?
            if (!seenResources.Add(resourceKey))
            {
                // Track it as a duplicate, and only report the validation failure first time
                if (duplicateResources.Add(resourceKey))
                {
                    context.AddFailure(
                        propertyName,
                        "Only unique resource claims can be added. The following is a duplicate resource: '{ResourceClaimName}'."
                    );
                }
            }
        }
    }

    private void ValidateChildren<T>(
        List<string> actionNames,
        List<string> authorizationStrategyNames,
        ResourceClaim resourceClaim,
        Dictionary<string, string?> parentClaimByResourceClaim,
        ValidationContext<T> context,
        string? claimSetName,
        string propertyName
    )
    {
        if (resourceClaim.Children.Count != 0)
        {
            foreach (var childResourceClaim in resourceClaim.Children)
            {
                // Locate the existing hierarchy tuple (existence check for child will be performed in Validate call below)
                if (
                    parentClaimByResourceClaim.TryGetValue(
                        childResourceClaim.Name!,
                        out string? expectedParentResourceName
                    )
                )
                {
                    context.MessageFormatter.AppendArgument("ChildResource", childResourceClaim.Name);

                    if (expectedParentResourceName == null)
                    {
                        context.AddFailure(
                            propertyName,
                            "'{ChildResource}' can not be added as a child resource."
                        );
                    }
                    else if (
                        !string.Equals(
                            resourceClaim.Name,
                            expectedParentResourceName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        context.MessageFormatter.AppendArgument(
                            "CorrectParentResource",
                            expectedParentResourceName
                        );
                        context.AddFailure(
                            propertyName,
                            "Child resource: '{ChildResource}' added to the wrong parent resource. Correct parent resource is: '{CorrectParentResource}'."
                        );
                    }
                }

                Validate(
                    actionNames,
                    authorizationStrategyNames,
                    childResourceClaim,
                    parentClaimByResourceClaim,
                    context,
                    claimSetName
                );
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

    private static void ValidateActions<T>(
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
