// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
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
        string? resourceClaimName = resourceClaim.ClaimName ?? resourceClaim.Name;

        context.MessageFormatter.AppendArgument("Name", claimSetName);
        context.MessageFormatter.AppendArgument("ResourceClaimName", resourceClaimName);

        ValidateResourceClaimExists(context, resourceClaimName, parentClaimByResourceClaim, PropertyName);

        ValidateNoDuplicateResourceClaims(resourceClaimName, context, PropertyName);
        ValidateActions(resourceClaim.Actions, actionNames, context, PropertyName);
        ValidateAuthStrategiesOverride(authorizationStrategyNames, resourceClaim, context, PropertyName);
        ValidateParentClaimName(resourceClaim, parentClaimByResourceClaim, context);

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

    private static void ValidateResourceClaimExists<T>(
        ValidationContext<T> context,
        string? resourceClaimName,
        IDictionary<string, string?> parentClaimByResourceClaim,
        string propertyName
    )
    {
        // Use propertyName to satisfy analyzer when the method intentionally records warnings instead of failures
        _ = propertyName;

        if (
            string.IsNullOrWhiteSpace(resourceClaimName)
            || !parentClaimByResourceClaim.ContainsKey(resourceClaimName)
        )
        {
            // Record skipped resource claim as a warning for later processing (do not fail validation)
            if (
                !context.RootContextData.TryGetValue("SkippedResourceClaims", out var skippedObj)
                || skippedObj is not List<string> skippedList
            )
            {
                skippedList = new List<string>();
                context.RootContextData["SkippedResourceClaims"] = skippedList;
            }

            skippedList.Add(resourceClaimName ?? string.Empty);
            return;
        }
    }

    private static void ValidateParentClaimName<T>(
        ResourceClaim resourceClaim,
        IReadOnlyDictionary<string, string?> parentClaimByResourceClaim,
        ValidationContext<T> context
    )
    {
        string? resourceClaimName = resourceClaim.ClaimName ?? resourceClaim.Name;

        if (
            string.IsNullOrWhiteSpace(resourceClaimName)
            || !parentClaimByResourceClaim.TryGetValue(resourceClaimName, out var expectedParentClaimName)
        )
        {
            return;
        }

        if (
            string.Equals(
                resourceClaim.ParentClaimName,
                expectedParentClaimName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(resourceClaim.ParentClaimName)
            && string.IsNullOrWhiteSpace(expectedParentClaimName)
        )
        {
            return;
        }

        if (
            !context.RootContextData.TryGetValue("ParentWarnings", out var parentWarningsObject)
            || parentWarningsObject is not List<string> parentWarnings
        )
        {
            parentWarnings = [];
            context.RootContextData["ParentWarnings"] = parentWarnings;
        }

        parentWarnings.Add(
            $"Child resource '{resourceClaimName}' added to the wrong parent resource. Correct parent resource is: '{expectedParentClaimName}'."
        );
    }

    private static void ValidateNoDuplicateResourceClaims<T>(
        string? resourceClaimName,
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
            seenResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            rootData["SeenResourceClaims"] = seenResources;
        }

        if (
            !rootData.TryGetValue("DuplicateResources", out var duplicatesObject)
            || duplicatesObject is not HashSet<string> duplicateResources
        )
        {
            duplicateResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            rootData["DuplicateResources"] = duplicateResources;
        }

        string? resourceKey = resourceClaimName;

        if (
            !string.IsNullOrWhiteSpace(resourceKey)
            && !seenResources.Add(resourceKey)
            && duplicateResources.Add(resourceKey)
        )
        {
            context.AddFailure(
                propertyName,
                "Only unique resource claims can be added. The following is a duplicate resource: '{ResourceClaimName}'."
            );
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
        // Use propertyName to satisfy analyzer when warnings are recorded instead of validation failures
        _ = propertyName;

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
                        // Record a warning for a child that cannot be added as a child resource
                        if (
                            !context.RootContextData.TryGetValue("ParentWarnings", out var pwObj)
                            || pwObj is not List<string> parentWarnings
                        )
                        {
                            parentWarnings = new List<string>();
                            context.RootContextData["ParentWarnings"] = parentWarnings;
                        }

                        parentWarnings.Add(
                            $"Child resource '{childResourceClaim.Name}' cannot be added as a child resource to '{resourceClaim.Name}'."
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
                        // Record a warning for parent mismatch instead of failing validation
                        if (
                            !context.RootContextData.TryGetValue("ParentWarnings", out var pmObj)
                            || pmObj is not List<string> parentWarnings
                        )
                        {
                            parentWarnings = new List<string>();
                            context.RootContextData["ParentWarnings"] = parentWarnings;
                        }

                        parentWarnings.Add(
                            $"Child resource '{childResourceClaim.Name}' added to the wrong parent resource. Correct parent resource is: '{expectedParentResourceName}'."
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
        if (resourceClaim.AuthorizationStrategyOverrides.Count != 0)
        {
            foreach (var authStrategyOverrideWithAction in resourceClaim.AuthorizationStrategyOverrides)
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
