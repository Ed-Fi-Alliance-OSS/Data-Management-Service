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
    public bool IsRelationshipWithEdOrg =>
        Enumerable.Any(
            authorizationStrategyEvaluators,
            a => Enumerable.Any(a.Filters, f => f.FilterPath == "EducationOrganization")
        );

    public ResourceAuthorizationResult Authorize(string[] namespaces, long[] educationOrganizationIds)
    {
        var andFilters = new List<(AuthorizationFilter Filter, bool IsAuthorized)>();
        var orFilters = new List<(AuthorizationFilter Filter, bool IsAuthorized)>();

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

        if (orFilters.Any() && orFilters.TrueForAll(f => !f.IsAuthorized))
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
        string[] values = [];

        if (filter.FilterPath == "Namespace")
        {
            values = namespaces;
        }
        else if (filter.FilterPath == "EducationOrganization")
        {
            values = educationOrganizationIds.Select(e => e.ToString()).ToArray();
        }

        return filter.Comparison switch
        {
            FilterComparison.Equals => Array.Exists(values, v => v.Equals(filter.Value)),
            FilterComparison.StartsWith => Array.Exists(values, v => v.StartsWith(filter.Value)),
            _ => false,
        };
    }

    private ResourceAuthorizationResult.NotAuthorized CreateNotAuthorizedResult(
        IEnumerable<(AuthorizationFilter Filter, bool IsAuthorized)> evaluations
    )
    {
        var claimValues = authorizationStrategyEvaluators
            .SelectMany(e => e.Filters.Select(f => $"'{f.Value}'"))
            .Distinct();

        var errorMessages = evaluations
            .Where(e => !e.IsAuthorized)
            .Select(e => e.Filter.ErrorMessageTemplate.Replace("{claims}", string.Join(", ", claimValues)))
            .ToArray();

        return new ResourceAuthorizationResult.NotAuthorized(errorMessages);
    }
}
