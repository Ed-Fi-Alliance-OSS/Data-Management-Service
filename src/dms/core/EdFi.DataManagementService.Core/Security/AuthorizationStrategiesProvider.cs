// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

public interface IAuthorizationStrategiesProvider
{
    IList<string> GetAuthorizationStrategies(ResourceClaim resourceClaim, string actionName);
}

public class AuthorizationStrategiesProvider() : IAuthorizationStrategiesProvider
{
    /// <summary>
    /// Gets list of authorization strategies for the resource claim action
    /// </summary>
    /// <param name="resourceClaim"></param>
    /// <param name="actionName"></param>
    /// <returns></returns>
    public IList<string> GetAuthorizationStrategies(ResourceClaim resourceClaim, string actionName)
    {
        List<string> authorizationStrategyList = [];
        List<ResourceClaimActionAuthStrategies> authStrategyOverrides =
            resourceClaim.AuthorizationStrategyOverridesForCRUD;
        List<ResourceClaimActionAuthStrategies> defaultAuthStrategies =
            resourceClaim.DefaultAuthorizationStrategiesForCRUD;

        ResourceClaimActionAuthStrategies? authStrategiesOverridesForAction =
            authStrategyOverrides.SingleOrDefault(x =>
                x != null
                && string.Equals(x.ActionName, actionName, StringComparison.InvariantCultureIgnoreCase)
            );

        if (
            authStrategiesOverridesForAction != null
            && authStrategiesOverridesForAction.AuthorizationStrategies != null
        )
        {
            authorizationStrategyList = authStrategiesOverridesForAction
                .AuthorizationStrategies.Select(x => x.AuthStrategyName)
                .ToList();
        }

        if (authorizationStrategyList.Count == 0)
        {
            ResourceClaimActionAuthStrategies? defaultAuthStrategiesForAction =
                defaultAuthStrategies.SingleOrDefault(x =>
                    x != null
                    && string.Equals(x.ActionName, actionName, StringComparison.InvariantCultureIgnoreCase)
                );

            if (
                defaultAuthStrategiesForAction != null
                && defaultAuthStrategiesForAction.AuthorizationStrategies != null
            )
            {
                authorizationStrategyList = defaultAuthStrategiesForAction
                    .AuthorizationStrategies.Select(x => x.AuthStrategyName)
                    .ToList();
            }
        }
        return authorizationStrategyList;
    }
}
