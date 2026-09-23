// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using Microsoft.Extensions.Logging;
using ActionModel = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public sealed class ClaimSetResourceActionMutationWorkflow(
    IReadOnlyList<ActionModel> configuredActions,
    IClaimsHierarchyRepository claimsHierarchyRepository,
    IClaimsHierarchyManager claimsHierarchyManager,
    Func<Task<DbConnection>> createOpenConnection,
    Func<
        DbConnection,
        DbTransaction,
        int,
        Task<ClaimSetResourceActionMutationWorkflow.ClaimSetMutationLookupResult?>
    > loadClaimSet,
    Func<DbConnection, DbTransaction, Task<List<ResourceClaimMetadataRow>>> loadResourceClaimMetadata,
    Func<Task<AuthorizationStrategyGetResult>> loadAuthorizationStrategies,
    ILogger logger
)
{
    public async Task<ClaimSetResourceActionMutationResult> GrantResourceClaimActions(
        ResourceClaimActionMutationCommand command
    )
    {
        ClaimSetResourceActionMutationResult? validationResult = ResolveActionNames(
            command,
            out List<string> actionNames
        );
        if (validationResult is not null)
        {
            return validationResult;
        }

        return await MutateClaimSetResourceActions(
            command.ClaimSetId,
            command.ResourceClaimId,
            (claimSetName, resourceClaimName, claims) =>
                claimsHierarchyManager.ReplaceClaimSetResourceActions(
                    claimSetName,
                    resourceClaimName,
                    actionNames,
                    claims
                )
                    ? new ClaimSetResourceActionMutationResult.Success()
                    : new ClaimSetResourceActionMutationResult.FailureResourceClaimNotFound()
        );
    }

    public Task<ClaimSetResourceActionMutationResult> ModifyResourceClaimActions(
        ResourceClaimActionMutationCommand command
    ) => GrantResourceClaimActions(command);

    public async Task<ClaimSetResourceActionMutationResult> RevokeResourceClaimActions(
        int claimSetId,
        int resourceClaimId
    )
    {
        return await MutateClaimSetResourceActions(
            claimSetId,
            resourceClaimId,
            (claimSetName, resourceClaimName, claims) =>
                claimsHierarchyManager.RemoveClaimSetResourceActions(claimSetName, resourceClaimName, claims)
                    ? new ClaimSetResourceActionMutationResult.Success()
                    : new ClaimSetResourceActionMutationResult.FailureTargetAssociationNotFound()
        );
    }

    public async Task<ClaimSetResourceActionMutationResult> OverrideAuthorizationStrategy(
        AuthorizationStrategyOverrideCommand command
    )
    {
        if (!TryResolveActionName(command.ActionName, out string actionName))
        {
            return new ClaimSetResourceActionMutationResult.FailureInvalidAction(command.ActionName);
        }

        AuthorizationStrategyGetResult configuredStrategiesResult = await loadAuthorizationStrategies();
        if (configuredStrategiesResult is not AuthorizationStrategyGetResult.Success success)
        {
            return configuredStrategiesResult switch
            {
                AuthorizationStrategyGetResult.FailureUnknown failure =>
                    new ClaimSetResourceActionMutationResult.FailureUnknown(failure.FailureMessage),
                _ => new ClaimSetResourceActionMutationResult.FailureUnknown(
                    $"Unhandled authorization strategy result of type '{configuredStrategiesResult.GetType().Name}'"
                ),
            };
        }

        List<AuthorizationStrategyLookup> configuredStrategies = success.AuthorizationStrategy
            .Select(strategy => new AuthorizationStrategyLookup(strategy.Id, strategy.AuthorizationStrategyName))
            .ToList();
        ClaimSetResourceActionMutationResult? validationResult = ResolveAuthorizationStrategyNames(
            command,
            configuredStrategies,
            out List<string> authorizationStrategyNames
        );
        if (validationResult is not null)
        {
            return validationResult;
        }

        return await MutateClaimSetResourceActions(
            command.ClaimSetId,
            command.ResourceClaimId,
            (claimSetName, resourceClaimName, claims) =>
                claimsHierarchyManager.GetClaimSetResourceActionStatus(
                    claimSetName,
                    resourceClaimName,
                    actionName,
                    claims
                ) switch
                {
                    ClaimSetResourceActionStatus.Enabled
                        when claimsHierarchyManager.OverrideClaimSetResourceActionStrategies(
                            claimSetName,
                            resourceClaimName,
                            actionName,
                            authorizationStrategyNames,
                            claims
                        ) => new ClaimSetResourceActionMutationResult.Success(),
                    ClaimSetResourceActionStatus.Disabled =>
                        new ClaimSetResourceActionMutationResult.FailureInvalidAction(actionName),
                    _ => new ClaimSetResourceActionMutationResult.FailureTargetAssociationNotFound(),
                }
        );
    }

    public async Task<ClaimSetResourceActionMutationResult> ResetAuthorizationStrategies(
        int claimSetId,
        int resourceClaimId
    )
    {
        return await MutateClaimSetResourceActions(
            claimSetId,
            resourceClaimId,
            (claimSetName, resourceClaimName, claims) =>
                claimsHierarchyManager.ResetClaimSetResourceActionStrategies(
                    claimSetName,
                    resourceClaimName,
                    claims
                )
                    ? new ClaimSetResourceActionMutationResult.Success()
                    : new ClaimSetResourceActionMutationResult.FailureTargetAssociationNotFound()
        );
    }

    private async Task<ClaimSetResourceActionMutationResult> MutateClaimSetResourceActions(
        int claimSetId,
        int resourceClaimId,
        Func<string, string, List<Claim>, ClaimSetResourceActionMutationResult> mutate
    )
    {
        await using DbConnection connection = await createOpenConnection();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();

        try
        {
            ClaimSetMutationLookupResult? claimSet = await loadClaimSet(connection, transaction, claimSetId);

            if (claimSet is null)
            {
                return await Rollback(new ClaimSetResourceActionMutationResult.FailureClaimSetNotFound());
            }

            if (claimSet.IsSystemReserved)
            {
                return await Rollback(new ClaimSetResourceActionMutationResult.FailureSystemReserved());
            }

            ClaimsHierarchyGetResult hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy(
                transaction
            );
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchy)
            {
                return await Rollback(MapHierarchyFailure(hierarchyResult));
            }

            List<ResourceClaimMetadataRow> metadata = await loadResourceClaimMetadata(
                connection,
                transaction
            );
            ResourceClaimMetadataResolveResult resourceClaimResult = ResourceClaimMetadataResolver.Resolve(
                resourceClaimId,
                hierarchy.Claims,
                metadata
            );
            if (resourceClaimResult is not ResourceClaimMetadataResolveResult.Success resourceClaim)
            {
                return await Rollback(MapResourceClaimFailure(resourceClaimResult));
            }

            ClaimSetResourceActionMutationResult mutationResult = mutate(
                claimSet.ClaimSetName,
                resourceClaim.ClaimName,
                hierarchy.Claims
            );
            if (mutationResult is not ClaimSetResourceActionMutationResult.Success)
            {
                return await Rollback(mutationResult);
            }

            ClaimsHierarchySaveResult saveResult = await claimsHierarchyRepository.SaveClaimsHierarchy(
                hierarchy.Claims,
                hierarchy.LastModifiedDate,
                transaction
            );
            ClaimSetResourceActionMutationResult result = MapSaveFailure(saveResult);
            if (result is not ClaimSetResourceActionMutationResult.Success)
            {
                return await Rollback(result);
            }

            await transaction.CommitAsync();
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mutate claim set resource actions failure");
            await transaction.RollbackAsync();
            return new ClaimSetResourceActionMutationResult.FailureUnknown(ex.Message);
        }

        async Task<ClaimSetResourceActionMutationResult> Rollback(ClaimSetResourceActionMutationResult result)
        {
            await transaction.RollbackAsync();
            return result;
        }
    }

    private ClaimSetResourceActionMutationResult? ResolveActionNames(
        ResourceClaimActionMutationCommand command,
        out List<string> canonicalActionNames
    )
    {
        canonicalActionNames = [];
        ClaimSetResourceActionMutationResult? validationResult = ResolveActionNames(
            command.SuppliedActionNames,
            out _
        );
        if (validationResult is not null)
        {
            return validationResult;
        }

        return ResolveActionNames(command.EnabledActionNames, out canonicalActionNames);
    }

    private ClaimSetResourceActionMutationResult? ResolveActionNames(
        IReadOnlyList<string> actionNames,
        out List<string> canonicalActionNames
    )
    {
        canonicalActionNames = [];

        foreach (string actionName in actionNames)
        {
            if (!TryResolveActionName(actionName, out string canonicalActionName))
            {
                return new ClaimSetResourceActionMutationResult.FailureInvalidAction(actionName);
            }

            canonicalActionNames.Add(canonicalActionName);
        }

        return null;
    }

    private bool TryResolveActionName(string actionName, out string canonicalActionName)
    {
        ActionModel? configuredAction = configuredActions.SingleOrDefault(action =>
            action.Name.Equals(actionName, StringComparison.OrdinalIgnoreCase)
        );
        if (configuredAction is null)
        {
            canonicalActionName = string.Empty;
            return false;
        }

        canonicalActionName = configuredAction.Name;
        return true;
    }

    private static ClaimSetResourceActionMutationResult? ResolveAuthorizationStrategyNames(
        AuthorizationStrategyOverrideCommand command,
        IReadOnlyList<AuthorizationStrategyLookup> configuredStrategies,
        out List<string> canonicalStrategyNames
    )
    {
        Dictionary<string, AuthorizationStrategyLookup> strategiesByName = configuredStrategies.ToDictionary(
            strategy => strategy.AuthorizationStrategyName,
            StringComparer.OrdinalIgnoreCase
        );
        Dictionary<int, AuthorizationStrategyLookup> strategiesById = configuredStrategies.ToDictionary(
            strategy => strategy.Id
        );
        canonicalStrategyNames = [];

        foreach (string strategyName in command.AuthorizationStrategyNames)
        {
            if (!strategiesByName.TryGetValue(strategyName, out AuthorizationStrategyLookup? strategy))
            {
                return new ClaimSetResourceActionMutationResult.FailureInvalidAuthorizationStrategy(
                    strategyName
                );
            }

            canonicalStrategyNames.Add(strategy.AuthorizationStrategyName);
        }

        var canonicalStrategyNamesFromIds = new List<string>();
        foreach (int strategyId in command.AuthStrategyIds)
        {
            if (!strategiesById.TryGetValue(strategyId, out AuthorizationStrategyLookup? strategy))
            {
                return new ClaimSetResourceActionMutationResult.FailureInvalidAuthorizationStrategy(
                    strategyId.ToString()
                );
            }

            canonicalStrategyNamesFromIds.Add(strategy.AuthorizationStrategyName);
        }

        if (
            canonicalStrategyNames.Count > 0
            && canonicalStrategyNamesFromIds.Count > 0
            && !new HashSet<string>(canonicalStrategyNames, StringComparer.Ordinal).SetEquals(
                canonicalStrategyNamesFromIds
            )
        )
        {
            return new ClaimSetResourceActionMutationResult.FailureAuthorizationStrategyMismatch();
        }

        if (canonicalStrategyNames.Count == 0)
        {
            canonicalStrategyNames.AddRange(canonicalStrategyNamesFromIds);
        }

        return null;
    }

    private static ClaimSetResourceActionMutationResult MapHierarchyFailure(ClaimsHierarchyGetResult result)
    {
        return result switch
        {
            ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound =>
                new ClaimSetResourceActionMutationResult.FailureMultipleHierarchiesFound(),
            ClaimsHierarchyGetResult.FailureUnknown failure =>
                new ClaimSetResourceActionMutationResult.FailureUnknown(failure.FailureMessage),
            ClaimsHierarchyGetResult.FailureHierarchyNotFound =>
                new ClaimSetResourceActionMutationResult.FailureUnknown("Claims hierarchy not found."),
            _ => new ClaimSetResourceActionMutationResult.FailureUnknown(
                $"Unhandled ClaimsHierarchyGetResult of type '{result.GetType().Name}'"
            ),
        };
    }

    private static ClaimSetResourceActionMutationResult MapResourceClaimFailure(
        ResourceClaimMetadataResolveResult result
    )
    {
        return result switch
        {
            ResourceClaimMetadataResolveResult.FailureResourceClaimNotFound =>
                new ClaimSetResourceActionMutationResult.FailureResourceClaimNotFound(),
            ResourceClaimMetadataResolveResult.FailureProjectionIntegrity failure =>
                new ClaimSetResourceActionMutationResult.FailureUnknown(failure.FailureMessage),
            _ => new ClaimSetResourceActionMutationResult.FailureUnknown(
                $"Unhandled ResourceClaimMetadataResolveResult of type '{result.GetType().Name}'"
            ),
        };
    }

    private static ClaimSetResourceActionMutationResult MapSaveFailure(ClaimsHierarchySaveResult result)
    {
        return result switch
        {
            ClaimsHierarchySaveResult.Success => new ClaimSetResourceActionMutationResult.Success(),
            ClaimsHierarchySaveResult.FailureMultipleHierarchiesFound =>
                new ClaimSetResourceActionMutationResult.FailureMultipleHierarchiesFound(),
            ClaimsHierarchySaveResult.FailureMultiUserConflict =>
                new ClaimSetResourceActionMutationResult.FailureMultiUserConflict(),
            ClaimsHierarchySaveResult.FailureUnknown failure =>
                new ClaimSetResourceActionMutationResult.FailureUnknown(failure.FailureMessage),
            _ => new ClaimSetResourceActionMutationResult.FailureUnknown(
                $"Unhandled ClaimsHierarchySaveResult of type '{result.GetType().Name}'"
            ),
        };
    }

    public sealed record ClaimSetMutationLookupResult(string ClaimSetName, bool IsSystemReserved);

    public sealed record AuthorizationStrategyLookup(int Id, string AuthorizationStrategyName);
}
