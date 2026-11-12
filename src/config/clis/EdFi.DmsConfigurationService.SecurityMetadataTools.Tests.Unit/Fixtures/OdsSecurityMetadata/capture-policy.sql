-- SPDX-License-Identifier: Apache-2.0
-- Independent, read-only source evidence. Does not call the XML exporter or converter.
SET NOCOUNT ON;
SELECT
    (SELECT ResourceClaimId, ClaimName, ParentResourceClaimId
     FROM dbo.ResourceClaims ORDER BY ResourceClaimId FOR JSON PATH, INCLUDE_NULL_VALUES) AS claims,
    (SELECT ClaimSetId, ClaimSetName FROM dbo.ClaimSets ORDER BY ClaimSetId FOR JSON PATH) AS claimSets,
    (SELECT ActionId, ActionName FROM dbo.Actions ORDER BY ActionId FOR JSON PATH) AS actions,
    (SELECT AuthorizationStrategyId, AuthorizationStrategyName
     FROM dbo.AuthorizationStrategies ORDER BY AuthorizationStrategyId FOR JSON PATH) AS strategies,
    (SELECT ResourceClaimActionId, ResourceClaimId, ActionId, ValidationRuleSetName
     FROM dbo.ResourceClaimActions ORDER BY ResourceClaimActionId FOR JSON PATH, INCLUDE_NULL_VALUES) AS defaults,
    (SELECT ResourceClaimActionId, AuthorizationStrategyId
     FROM dbo.ResourceClaimActionAuthorizationStrategies
     ORDER BY ResourceClaimActionId, AuthorizationStrategyId FOR JSON PATH) AS defaultStrategies,
    (SELECT ClaimSetResourceClaimActionId, ClaimSetId, ResourceClaimId, ActionId, ValidationRuleSetNameOverride
     FROM dbo.ClaimSetResourceClaimActions
     ORDER BY ClaimSetResourceClaimActionId FOR JSON PATH, INCLUDE_NULL_VALUES) AS grants,
    (SELECT ClaimSetResourceClaimActionId, AuthorizationStrategyId
     FROM dbo.ClaimSetResourceClaimActionAuthorizationStrategyOverrides
     ORDER BY ClaimSetResourceClaimActionId, AuthorizationStrategyId FOR JSON PATH) AS overrides,
    (SELECT ScriptName FROM dbo.DeployJournal ORDER BY Id FOR JSON PATH) AS journal
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
