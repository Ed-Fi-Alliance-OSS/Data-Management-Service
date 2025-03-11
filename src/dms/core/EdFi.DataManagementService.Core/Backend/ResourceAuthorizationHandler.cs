// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// The ResourceAuthorizationHandler implementation that
/// interrogates a resources securityElements to determine if the
/// filters in the provided authorizationStrategy are satisfied.
/// </summary>
public class ResourceAuthorizationHandler(
    AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
    ILogger logger
) : IResourceAuthorizationHandler
{
    private static class FilterPaths
    {
        public const string Namespace = "Namespace";
        public const string EducationOrganization = "EducationOrganization";
    }

    public bool IsRelationshipWithEdOrg =>
        Enumerable.Any(
            authorizationStrategyEvaluators,
            a => Enumerable.Any(a.Filters, f => f.FilterPath == FilterPaths.EducationOrganization)
        );

    public ResourceAuthorizationResult Authorize(string[] namespaces, long[] educationOrganizationIds)
    {
        List<(AuthorizationFilter Filter, bool IsAuthorized)> andFilters = [];
        List<(AuthorizationFilter Filter, bool IsAuthorized)> orFilters = [];

        foreach (var evaluator in authorizationStrategyEvaluators)
        {
            foreach (var filter in evaluator.Filters)
            {
                logger.LogDebug("Evaluating filter: {Filter}", filter);
                bool isAuthorized = EvaluateFilter(filter, namespaces, educationOrganizationIds);

                if (evaluator.Operator == FilterOperator.And)
                {
                    andFilters.Add((filter, isAuthorized));
                }
                else
                {
                    orFilters.Add((filter, isAuthorized));
                }
            }
        }

        if (andFilters.Exists(f => !f.IsAuthorized))
        {
            return CreateNotAuthorizedResult(andFilters);
        }

        if (orFilters.Any() && Enumerable.All(orFilters, f => !f.IsAuthorized))
        {
            return CreateNotAuthorizedResult(orFilters);
        }

        return new ResourceAuthorizationResult.Authorized();
    }

    private static bool EvaluateFilter(
        AuthorizationFilter filter,
        string[] namespaces,
        long[] educationOrganizationIds
    )
    {
        switch (filter.FilterPath)
        {
            case FilterPaths.Namespace:
                return filter.Comparison switch
                {
                    FilterComparison.Equals => namespaces.Contains(filter.Value),
                    FilterComparison.StartsWith => namespaces
                        .ToList()
                        .Exists(v => v.StartsWith(filter.Value)),
                    _ => false,
                };

            case FilterPaths.EducationOrganization when long.TryParse(filter.Value, out long edOrgIdValue):
                return filter.Comparison switch
                {
                    FilterComparison.Equals => educationOrganizationIds.Contains(edOrgIdValue),
                    _ => false,
                };

            default:
                return false;
        }
    }

    private ResourceAuthorizationResult.NotAuthorized CreateNotAuthorizedResult(
        IEnumerable<(AuthorizationFilter Filter, bool IsAuthorized)> evaluations
    )
    {
        List<string> failedPaths = evaluations
            .SelectMany(e => new[] { e.Filter })
            .Select(f => f.FilterPath)
            .Distinct()
            .ToList();

        Dictionary<string, List<string>> claimsByPath = failedPaths.ToDictionary(
            path => path,
            path =>
                authorizationStrategyEvaluators
                    .SelectMany(e => e.Filters)
                    .Where(f => f.FilterPath == path)
                    .Select(f => $"'{f.Value}'")
                    .Distinct()
                    .ToList()
        );

        string[] errorMessages = evaluations
            .Where(e => !e.IsAuthorized)
            .Select(e =>
            {
                string claims = string.Join(", ", claimsByPath[e.Filter.FilterPath]);
                return e.Filter.ErrorMessageTemplate.Replace("{claims}", claims);
            })
            .Distinct()
            .ToArray();

        return new ResourceAuthorizationResult.NotAuthorized(errorMessages);
    }
}
