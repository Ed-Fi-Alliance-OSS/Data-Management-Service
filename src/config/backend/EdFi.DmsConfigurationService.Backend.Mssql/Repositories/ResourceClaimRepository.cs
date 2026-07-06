// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

#pragma warning disable S1128 // Remove unnecessary usings - False positive: QueryAsync needs Dapper
using Dapper;
#pragma warning restore S1128
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Action = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;
using AuthorizationStrategy = EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public class ResourceClaimRepository(
    IOptions<DatabaseOptions> databaseOptions,
    IClaimsHierarchyRepository claimsHierarchyRepository,
    IClaimSetRepository claimSetRepository,
    ILogger<ResourceClaimRepository> logger
) : IResourceClaimRepository
{
    private sealed record ResourceClaimMetadataRow(long Id, string ResourceName, string ClaimName);

    private sealed record ProjectionResult(
        List<ResourceClaimResponse> Roots,
        IReadOnlyList<(ResourceClaimResponse Node, Claim OriginalClaim)> AllNodes
    );

    // Filter to TenantId IS NULL: ResourceClaim seed rows are global bootstrap metadata with no tenant scope.
    private async Task<List<ResourceClaimMetadataRow>> LoadResourceClaimMetadata()
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        var rows = await connection.QueryAsync(
            "SELECT Id AS id, ResourceName AS resourcename, ClaimName AS claimname FROM dmscs.ResourceClaim WHERE TenantId IS NULL"
        );
        return rows.Select(r => new ResourceClaimMetadataRow(
                (long)r.id,
                (string)r.resourcename,
                (string)r.claimname
            ))
            .ToList();
    }

    private static ProjectionResult? BuildProjectedHierarchy(
        List<Claim> hierarchy,
        Dictionary<string, ResourceClaimMetadataRow> metadataByUri,
        out string? failureMessage
    )
    {
        failureMessage = null;
        var roots = new List<ResourceClaimResponse>();
        var allNodes = new List<(ResourceClaimResponse Node, Claim OriginalClaim)>();

        foreach (var claim in hierarchy)
        {
            var node = BuildProjectedNode(claim, null, metadataByUri, allNodes, out failureMessage);
            if (node is null)
            {
                return null;
            }
            roots.Add(node);
        }

        return new ProjectionResult(roots, allNodes);
    }

    private static ResourceClaimResponse? BuildProjectedNode(
        Claim claim,
        ResourceClaimResponse? parent,
        Dictionary<string, ResourceClaimMetadataRow> metadataByUri,
        List<(ResourceClaimResponse Node, Claim OriginalClaim)> allNodes,
        out string? failureMessage
    )
    {
        failureMessage = null;

        if (!metadataByUri.TryGetValue(claim.Name, out var metadata))
        {
            failureMessage = $"No ResourceClaim metadata found for claim URI '{claim.Name}'.";
            return null;
        }

        var node = new ResourceClaimResponse
        {
            Id = metadata.Id,
            Name = metadata.ResourceName,
            ParentId = parent?.Id ?? 0,
            ParentName = parent?.Name,
            Children = [],
        };

        allNodes.Add((node, claim));

        foreach (var child in claim.Claims)
        {
            var childNode = BuildProjectedNode(child, node, metadataByUri, allNodes, out failureMessage);
            if (childNode is null)
            {
                return null;
            }
            node.Children.Add(childNode);
        }

        return node;
    }

    internal static IEnumerable<T> ApplyPaging<T>(IEnumerable<T> items, int? offset, int? limit)
    {
        IEnumerable<T> result = items;
        if (offset.HasValue)
        {
            result = result.Skip(offset.Value);
        }
        if (limit.HasValue)
        {
            result = result.Take(limit.Value);
        }
        return result;
    }

    internal static IEnumerable<ResourceClaimResponse> SortAndPage(
        IEnumerable<ResourceClaimResponse> items,
        ResourceClaimQuery query
    )
    {
        bool desc = query.IsDescending;
        IEnumerable<ResourceClaimResponse> result = (query.OrderBy?.ToLowerInvariant() ?? "name") switch
        {
            "id" => desc ? items.OrderByDescending(r => r.Id) : items.OrderBy(r => r.Id),
            "parentid" => desc ? items.OrderByDescending(r => r.ParentId) : items.OrderBy(r => r.ParentId),
            "parentname" => desc
                ? items.OrderByDescending(r => r.ParentName, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(r => r.ParentName, StringComparer.OrdinalIgnoreCase),
            _ => desc
                ? items.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };
        return ApplyPaging(result, query.Offset, query.Limit);
    }

    internal static IEnumerable<ResourceClaimActionResponse> SortAndPage(
        IEnumerable<ResourceClaimActionResponse> items,
        ResourceClaimActionQuery query
    )
    {
        bool desc = query.IsDescending;
        IEnumerable<ResourceClaimActionResponse> result = (
            query.OrderBy?.ToLowerInvariant() ?? "resourceclaimid"
        ) switch
        {
            "resourcename" => desc
                ? items.OrderByDescending(r => r.ResourceName, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(r => r.ResourceName, StringComparer.OrdinalIgnoreCase),
            _ => desc
                ? items.OrderByDescending(r => r.ResourceClaimId)
                : items.OrderBy(r => r.ResourceClaimId),
        };
        return ApplyPaging(result, query.Offset, query.Limit);
    }

    internal static IEnumerable<ResourceClaimActionAuthStrategyResponse> SortAndPage(
        IEnumerable<ResourceClaimActionAuthStrategyResponse> items,
        ResourceClaimActionAuthStrategyQuery query
    )
    {
        bool desc = query.IsDescending;
        IEnumerable<ResourceClaimActionAuthStrategyResponse> result = (
            query.OrderBy?.ToLowerInvariant() ?? "resourceclaimid"
        ) switch
        {
            "resourcename" => desc
                ? items.OrderByDescending(r => r.ResourceName, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(r => r.ResourceName, StringComparer.OrdinalIgnoreCase),
            "claimname" => desc
                ? items.OrderByDescending(r => r.ClaimName, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(r => r.ClaimName, StringComparer.OrdinalIgnoreCase),
            _ => desc
                ? items.OrderByDescending(r => r.ResourceClaimId)
                : items.OrderBy(r => r.ResourceClaimId),
        };
        return ApplyPaging(result, query.Offset, query.Limit);
    }

    public async Task<ResourceClaimListResult> GetResourceClaims(ResourceClaimQuery query)
    {
        try
        {
            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
            {
                return hierarchyResult switch
                {
                    ClaimsHierarchyGetResult.FailureHierarchyNotFound =>
                        new ResourceClaimListResult.FailureHierarchyNotFound(),
                    _ => new ResourceClaimListResult.FailureUnknown("Failed to load claims hierarchy."),
                };
            }

            var metadata = await LoadResourceClaimMetadata();
            var metadataByUri = metadata.ToDictionary(m => m.ClaimName, m => m);

            var projection = BuildProjectedHierarchy(
                hierarchySuccess.Claims,
                metadataByUri,
                out var failureMessage
            );
            if (projection is null)
            {
                logger.LogError("Resource claim projection integrity failure: {Message}", failureMessage);
                return new ResourceClaimListResult.FailureProjectionIntegrity(failureMessage!);
            }

            IEnumerable<ResourceClaimResponse> roots = projection.Roots;
            if (query.Id.HasValue)
            {
                roots = roots.Where(r => r.Id == query.Id.Value);
            }
            if (query.Name is not null)
            {
                roots = roots.Where(r => r.Name.Equals(query.Name, StringComparison.OrdinalIgnoreCase));
            }

            return new ResourceClaimListResult.Success(SortAndPage(roots, query).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetResourceClaims failure");
            return new ResourceClaimListResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ResourceClaimGetResult> GetResourceClaim(long id)
    {
        try
        {
            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
            {
                return hierarchyResult switch
                {
                    ClaimsHierarchyGetResult.FailureHierarchyNotFound =>
                        new ResourceClaimGetResult.FailureHierarchyNotFound(),
                    _ => new ResourceClaimGetResult.FailureUnknown("Failed to load claims hierarchy."),
                };
            }

            var metadata = await LoadResourceClaimMetadata();
            var metadataByUri = metadata.ToDictionary(m => m.ClaimName, m => m);

            var projection = BuildProjectedHierarchy(
                hierarchySuccess.Claims,
                metadataByUri,
                out var failureMessage
            );
            if (projection is null)
            {
                logger.LogError("Resource claim projection integrity failure: {Message}", failureMessage);
                return new ResourceClaimGetResult.FailureProjectionIntegrity(failureMessage!);
            }

            var match = projection.AllNodes.FirstOrDefault(n => n.Node.Id == id);
            if (match == default)
            {
                return new ResourceClaimGetResult.FailureNotFound();
            }

            return new ResourceClaimGetResult.Success(match.Node);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetResourceClaim failure for id={Id}", id);
            return new ResourceClaimGetResult.FailureUnknown(ex.Message);
        }
    }

    private sealed record ResolvedClaimNode(
        ResourceClaimResponse Node,
        Claim OriginalClaim,
        Dictionary<string, Action> KnownActions,
        Dictionary<string, AuthorizationStrategy> KnownAuthStrategies
    );

    private abstract record ResolveClaimNodesResult
    {
        public sealed record Success(List<ResolvedClaimNode> Nodes) : ResolveClaimNodesResult;

        public sealed record FailureHierarchyNotFound() : ResolveClaimNodesResult;

        public sealed record FailureProjectionIntegrity(string FailureMessage) : ResolveClaimNodesResult;

        public sealed record FailureUnknown(string FailureMessage) : ResolveClaimNodesResult;
    }

    private async Task<ResolveClaimNodesResult> LoadAndResolveClaimNodesWithActions()
    {
        var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();
        if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
        {
            return hierarchyResult switch
            {
                ClaimsHierarchyGetResult.FailureHierarchyNotFound =>
                    new ResolveClaimNodesResult.FailureHierarchyNotFound(),
                _ => new ResolveClaimNodesResult.FailureUnknown("Failed to load claims hierarchy."),
            };
        }

        var metadata = await LoadResourceClaimMetadata();
        var metadataByUri = metadata.ToDictionary(m => m.ClaimName, m => m);

        var projection = BuildProjectedHierarchy(
            hierarchySuccess.Claims,
            metadataByUri,
            out var projectionFailure
        );
        if (projection is null)
        {
            logger.LogError("Resource claim projection integrity failure: {Message}", projectionFailure);
            return new ResolveClaimNodesResult.FailureProjectionIntegrity(projectionFailure!);
        }

        var knownActions = claimSetRepository
            .GetActions()
            .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

        var authStrategiesResult = await claimSetRepository.GetAuthorizationStrategies();
        if (authStrategiesResult is not AuthorizationStrategyGetResult.Success authStrategiesSuccess)
        {
            logger.LogError("Failed to load authorization strategies.");
            return new ResolveClaimNodesResult.FailureProjectionIntegrity(
                "Failed to load authorization strategies."
            );
        }
        var knownAuthStrategies = authStrategiesSuccess.AuthorizationStrategy.ToDictionary(
            s => s.AuthorizationStrategyName,
            s => s,
            StringComparer.OrdinalIgnoreCase
        );

        var resolvedNodes = new List<ResolvedClaimNode>();
        foreach (var (node, originalClaim) in projection.AllNodes)
        {
            if (originalClaim.DefaultAuthorization is null)
            {
                continue;
            }

            // Validate all actions and strategies exist
            foreach (var action in originalClaim.DefaultAuthorization.Actions)
            {
                if (!knownActions.TryGetValue(action.Name, out var knownAction))
                {
                    var failureMessage =
                        $"Action '{action.Name}' in DefaultAuthorization for claim '{originalClaim.Name}' could not be resolved.";
                    logger.LogError(
                        "Action '{ActionName}' in DefaultAuthorization for claim '{ClaimUri}' could not be resolved.",
                        action.Name,
                        originalClaim.Name
                    );
                    return new ResolveClaimNodesResult.FailureProjectionIntegrity(failureMessage);
                }

                var invalidStrategy = action.AuthorizationStrategies.Find(s =>
                    !knownAuthStrategies.ContainsKey(s.Name)
                );
                if (invalidStrategy is not null)
                {
                    var failureMessage =
                        $"Authorization strategy '{invalidStrategy.Name}' in DefaultAuthorization for claim '{originalClaim.Name}', action '{action.Name}' could not be resolved.";
                    logger.LogError(
                        "Authorization strategy '{StrategyName}' in DefaultAuthorization for claim '{ClaimUri}', action '{ActionName}' could not be resolved.",
                        invalidStrategy.Name,
                        originalClaim.Name,
                        action.Name
                    );
                    return new ResolveClaimNodesResult.FailureProjectionIntegrity(failureMessage);
                }
            }

            resolvedNodes.Add(new ResolvedClaimNode(node, originalClaim, knownActions, knownAuthStrategies));
        }

        return new ResolveClaimNodesResult.Success(resolvedNodes);
    }

    public async Task<ResourceClaimActionListResult> GetResourceClaimActions(ResourceClaimActionQuery query)
    {
        try
        {
            var resolveResult = await LoadAndResolveClaimNodesWithActions();
            if (resolveResult is not ResolveClaimNodesResult.Success resolveSuccess)
            {
                return resolveResult switch
                {
                    ResolveClaimNodesResult.FailureHierarchyNotFound =>
                        new ResourceClaimActionListResult.FailureHierarchyNotFound(),
                    ResolveClaimNodesResult.FailureProjectionIntegrity projectionFailure =>
                        new ResourceClaimActionListResult.FailureProjectionIntegrity(
                            projectionFailure.FailureMessage
                        ),
                    ResolveClaimNodesResult.FailureUnknown unknownFailure =>
                        new ResourceClaimActionListResult.FailureUnknown(unknownFailure.FailureMessage),
                    _ => new ResourceClaimActionListResult.FailureProjectionIntegrity(
                        "Failed to resolve resource claim actions."
                    ),
                };
            }

            var items = new List<ResourceClaimActionResponse>();
            foreach (var resolved in resolveSuccess.Nodes)
            {
                var actionNames = resolved
                    .OriginalClaim.DefaultAuthorization!.Actions.Select(action =>
                    {
                        var knownAction = resolved.KnownActions[action.Name];
                        return new ActionNameResponse { Name = knownAction.Name };
                    })
                    .ToList();

                items.Add(
                    new ResourceClaimActionResponse
                    {
                        ResourceClaimId = resolved.Node.Id,
                        ResourceName = resolved.Node.Name,
                        ClaimName = resolved.OriginalClaim.Name,
                        Actions = actionNames,
                    }
                );
            }

            IEnumerable<ResourceClaimActionResponse> result = items;
            if (query.ResourceName is not null)
            {
                result = result.Where(r =>
                    r.ResourceName.Equals(query.ResourceName, StringComparison.OrdinalIgnoreCase)
                );
            }

            return new ResourceClaimActionListResult.Success(SortAndPage(result, query).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetResourceClaimActions failure");
            return new ResourceClaimActionListResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ResourceClaimActionAuthStrategyListResult> GetResourceClaimActionAuthStrategies(
        ResourceClaimActionAuthStrategyQuery query
    )
    {
        try
        {
            var resolveResult = await LoadAndResolveClaimNodesWithActions();
            if (resolveResult is not ResolveClaimNodesResult.Success resolveSuccess)
            {
                return resolveResult switch
                {
                    ResolveClaimNodesResult.FailureHierarchyNotFound =>
                        new ResourceClaimActionAuthStrategyListResult.FailureHierarchyNotFound(),
                    ResolveClaimNodesResult.FailureProjectionIntegrity projectionFailure =>
                        new ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity(
                            projectionFailure.FailureMessage
                        ),
                    ResolveClaimNodesResult.FailureUnknown unknownFailure =>
                        new ResourceClaimActionAuthStrategyListResult.FailureUnknown(
                            unknownFailure.FailureMessage
                        ),
                    _ => new ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity(
                        "Failed to resolve resource claim action authorization strategies."
                    ),
                };
            }

            var items = new List<ResourceClaimActionAuthStrategyResponse>();
            foreach (var resolved in resolveSuccess.Nodes)
            {
                var actionsWithStrategies = resolved
                    .OriginalClaim.DefaultAuthorization!.Actions.Select(action =>
                    {
                        var knownAction = resolved.KnownActions[action.Name];
                        var strategies = action
                            .AuthorizationStrategies.Select(strategy =>
                            {
                                var knownStrategy = resolved.KnownAuthStrategies[strategy.Name];
                                return new AuthorizationStrategyForActionResponse
                                {
                                    AuthStrategyId = knownStrategy.Id,
                                    AuthStrategyName = knownStrategy.AuthorizationStrategyName,
                                };
                            })
                            .ToList();

                        return new ActionWithAuthorizationStrategyResponse
                        {
                            ActionId = knownAction.Id,
                            ActionName = knownAction.Name,
                            AuthorizationStrategies = strategies,
                        };
                    })
                    .ToList();

                items.Add(
                    new ResourceClaimActionAuthStrategyResponse
                    {
                        ResourceClaimId = resolved.Node.Id,
                        ResourceName = resolved.Node.Name,
                        ClaimName = resolved.OriginalClaim.Name,
                        AuthorizationStrategiesForActions = actionsWithStrategies,
                    }
                );
            }

            IEnumerable<ResourceClaimActionAuthStrategyResponse> result = items;
            if (query.ResourceName is not null)
            {
                result = result.Where(r =>
                    r.ResourceName.Equals(query.ResourceName, StringComparison.OrdinalIgnoreCase)
                );
            }

            return new ResourceClaimActionAuthStrategyListResult.Success(SortAndPage(result, query).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetResourceClaimActionAuthStrategies failure");
            return new ResourceClaimActionAuthStrategyListResult.FailureUnknown(ex.Message);
        }
    }
}
