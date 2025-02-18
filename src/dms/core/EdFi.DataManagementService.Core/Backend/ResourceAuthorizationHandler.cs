// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// The ResourceAuthorizationHandler implementation that
/// interrogates a resources securityElements to determine if the
/// filters in the provided authorizationStrategy are satisfied.
/// </summary>
/// <param name="authorizationStrategyEvaluators"></param>
/// <param name="logger"></param>
public class ResourceAuthorizationHandler(
    AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
    ILogger logger
) : IResourceAuthorizationHandler
{
    public ResourceAuthorizationResult Authorize(JsonNode securityElements)
    {
        List<KeyValuePair<AuthorizationFilter, bool>> andFilterEvaluations =
            new List<KeyValuePair<AuthorizationFilter, bool>>();
        List<KeyValuePair<AuthorizationFilter, bool>> orFilterEvaluations =
            new List<KeyValuePair<AuthorizationFilter, bool>>();

        foreach (var evaluator in authorizationStrategyEvaluators)
        {
            foreach (var filter in evaluator.Filters)
            {
                logger.LogDebug("Evaluating filter: {Filter}", filter);
                JsonArray? valuesArray = securityElements[filter.FilterPath]?.AsArray();
                string[] valuesStrings =
                    valuesArray?.Select(v => v?.ToString() ?? string.Empty).ToArray()
                    ?? Array.Empty<string>();

                switch (filter.Comparison)
                {
                    case FilterComparison.Equals:
                        if (evaluator.Operator == FilterOperator.And)
                        {
                            andFilterEvaluations.Add(
                                new KeyValuePair<AuthorizationFilter, bool>(
                                    filter,
                                    Array.FindIndex(valuesStrings, v => v.Equals(filter.Value)) >= 0
                                )
                            );
                        }
                        else
                        {
                            orFilterEvaluations.Add(
                                new KeyValuePair<AuthorizationFilter, bool>(
                                    filter,
                                    Array.FindIndex(valuesStrings, v => v.Equals(filter.Value)) >= 0
                                )
                            );
                        }
                        break;
                    case FilterComparison.StartsWith:
                        if (evaluator.Operator == FilterOperator.And)
                        {
                            andFilterEvaluations.Add(
                                new KeyValuePair<AuthorizationFilter, bool>(
                                    filter,
                                    Array.FindIndex(valuesStrings, v => v.StartsWith(filter.Value)) >= 0
                                )
                            );
                        }
                        else
                        {
                            orFilterEvaluations.Add(
                                new KeyValuePair<AuthorizationFilter, bool>(
                                    filter,
                                    Array.FindIndex(valuesStrings, v => v.StartsWith(filter.Value)) >= 0
                                )
                            );
                        }
                        break;
                }
            }
        }

        if (andFilterEvaluations.Exists(e => !e.Value))
        {
            return reportErrors(andFilterEvaluations);
        }

        if (orFilterEvaluations.Any() && orFilterEvaluations.TrueForAll(e => !e.Value))
        {
            return reportErrors(orFilterEvaluations);
        }

        return new ResourceAuthorizationResult.Authorized();

        ResourceAuthorizationResult reportErrors(List<KeyValuePair<AuthorizationFilter, bool>> evaluations)
        {
            var values = authorizationStrategyEvaluators
                .SelectMany(e => e.Filters.Select(f => $"'{f.Value}'"))
                .Distinct();

            var errors = evaluations
                .Where(e => !e.Value)
                .Select(e => e.Key.ErrorMessageTemplate.Replace("{claims}", string.Join(", ", values)));

            return new ResourceAuthorizationResult.NotAuthorized(errors.ToArray());
        }
    }
}
