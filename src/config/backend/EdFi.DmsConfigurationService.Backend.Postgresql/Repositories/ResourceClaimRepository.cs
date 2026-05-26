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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        var rows = await connection.QueryAsync(
            "SELECT Id, ResourceName, ClaimName FROM dmscs.ResourceClaim WHERE TenantId IS NULL"
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

    private static bool IsDescending(PagingQuery query) =>
        query.Direction is not null
        && (
            query.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase)
            || query.Direction.Equals("descending", StringComparison.OrdinalIgnoreCase)
        );

    private static IEnumerable<ResourceClaimResponse> SortAndPage(
        IEnumerable<ResourceClaimResponse> items,
        ResourceClaimQuery query
    )
    {
        bool desc = IsDescending(query);
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
        if (query.Offset.HasValue)
        {
            result = result.Skip(query.Offset.Value);
        }
        if (query.Limit.HasValue)
        {
            result = result.Take(query.Limit.Value);
        }
        return result;
    }

    private static IEnumerable<ResourceClaimActionResponse> SortAndPage(
        IEnumerable<ResourceClaimActionResponse> items,
        ResourceClaimActionQuery query
    )
    {
        bool desc = IsDescending(query);
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
        if (query.Offset.HasValue)
        {
            result = result.Skip(query.Offset.Value);
        }
        if (query.Limit.HasValue)
        {
            result = result.Take(query.Limit.Value);
        }
        return result;
    }

    private static IEnumerable<ResourceClaimActionAuthStrategyResponse> SortAndPage(
        IEnumerable<ResourceClaimActionAuthStrategyResponse> items,
        ResourceClaimActionAuthStrategyQuery query
    )
    {
        bool desc = IsDescending(query);
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
        if (query.Offset.HasValue)
        {
            result = result.Skip(query.Offset.Value);
        }
        if (query.Limit.HasValue)
        {
            result = result.Take(query.Limit.Value);
        }
        return result;
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

    public async Task<ResourceClaimActionListResult> GetResourceClaimActions(ResourceClaimActionQuery query)
    {
        try
        {
            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
            {
                return hierarchyResult switch
                {
                    ClaimsHierarchyGetResult.FailureHierarchyNotFound =>
                        new ResourceClaimActionListResult.FailureHierarchyNotFound(),
                    _ => new ResourceClaimActionListResult.FailureUnknown("Failed to load claims hierarchy."),
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
                return new ResourceClaimActionListResult.FailureProjectionIntegrity(failureMessage!);
            }

            var knownActions = claimSetRepository
                .GetActions()
                .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

            var items = new List<ResourceClaimActionResponse>();
            foreach (var (node, originalClaim) in projection.AllNodes)
            {
                if (originalClaim.DefaultAuthorization is null)
                {
                    continue;
                }

                var actionNames = new List<ActionNameResponse>();
#pragma warning disable S3267 // TryGetValue with early return cannot be replaced by LINQ Select
                foreach (var action in originalClaim.DefaultAuthorization.Actions)
                {
                    if (!knownActions.TryGetValue(action.Name, out var knownAction))
                    {
                        var msg =
                            $"Action '{action.Name}' in DefaultAuthorization for claim '{originalClaim.Name}' could not be resolved.";
                        logger.LogError(
                            "Action '{ActionName}' in DefaultAuthorization for claim '{ClaimUri}' could not be resolved.",
                            action.Name,
                            originalClaim.Name
                        );
                        return new ResourceClaimActionListResult.FailureProjectionIntegrity(msg);
                    }
                    actionNames.Add(new ActionNameResponse { Name = knownAction.Name });
                }
#pragma warning restore S3267

                items.Add(
                    new ResourceClaimActionResponse
                    {
                        ResourceClaimId = node.Id,
                        ResourceName = node.Name,
                        ClaimName = originalClaim.Name,
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
            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
            {
                return hierarchyResult switch
                {
                    ClaimsHierarchyGetResult.FailureHierarchyNotFound =>
                        new ResourceClaimActionAuthStrategyListResult.FailureHierarchyNotFound(),
                    _ => new ResourceClaimActionAuthStrategyListResult.FailureUnknown(
                        "Failed to load claims hierarchy."
                    ),
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
                return new ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity(
                    failureMessage!
                );
            }

            var knownActions = claimSetRepository
                .GetActions()
                .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

            var authStrategiesResult = await claimSetRepository.GetAuthorizationStrategies();
            if (authStrategiesResult is not AuthorizationStrategyGetResult.Success authStrategiesSuccess)
            {
                logger.LogError("Failed to load authorization strategies.");
                return new ResourceClaimActionAuthStrategyListResult.FailureUnknown(
                    "Failed to load authorization strategies."
                );
            }
            var knownAuthStrategies = authStrategiesSuccess.AuthorizationStrategy.ToDictionary(
                s => s.AuthorizationStrategyName,
                s => s,
                StringComparer.OrdinalIgnoreCase
            );

            var items = new List<ResourceClaimActionAuthStrategyResponse>();
            foreach (var (node, originalClaim) in projection.AllNodes)
            {
                if (originalClaim.DefaultAuthorization is null)
                {
                    continue;
                }

                var actionsWithStrategies = new List<ActionWithAuthorizationStrategyResponse>();
                foreach (var action in originalClaim.DefaultAuthorization.Actions)
                {
                    if (!knownActions.TryGetValue(action.Name, out var knownAction))
                    {
                        var msg =
                            $"Action '{action.Name}' in DefaultAuthorization for claim '{originalClaim.Name}' could not be resolved.";
                        logger.LogError(
                            "Action '{ActionName}' in DefaultAuthorization for claim '{ClaimUri}' could not be resolved.",
                            action.Name,
                            originalClaim.Name
                        );
                        return new ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity(msg);
                    }

                    // Validate all strategies exist before processing
                    var invalidStrategy = action.AuthorizationStrategies.Find(s =>
                        !knownAuthStrategies.ContainsKey(s.Name)
                    );
                    if (invalidStrategy is not null)
                    {
                        var msg =
                            $"Authorization strategy '{invalidStrategy.Name}' in DefaultAuthorization for claim '{originalClaim.Name}', action '{action.Name}' could not be resolved.";
                        logger.LogError(
                            "Authorization strategy '{StrategyName}' in DefaultAuthorization for claim '{ClaimUri}', action '{ActionName}' could not be resolved.",
                            invalidStrategy.Name,
                            originalClaim.Name,
                            action.Name
                        );
                        return new ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity(msg);
                    }

                    var strategies = action
                        .AuthorizationStrategies.Select(strategy =>
                        {
                            var knownStrategy = knownAuthStrategies[strategy.Name];
                            return new AuthorizationStrategyForActionResponse
                            {
                                AuthStrategyId = knownStrategy.Id,
                                AuthStrategyName = knownStrategy.AuthorizationStrategyName,
                            };
                        })
                        .ToList();

                    actionsWithStrategies.Add(
                        new ActionWithAuthorizationStrategyResponse
                        {
                            ActionId = knownAction.Id,
                            ActionName = knownAction.Name,
                            AuthorizationStrategies = strategies,
                        }
                    );
                }

                items.Add(
                    new ResourceClaimActionAuthStrategyResponse
                    {
                        ResourceClaimId = node.Id,
                        ResourceName = node.Name,
                        ClaimName = originalClaim.Name,
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
