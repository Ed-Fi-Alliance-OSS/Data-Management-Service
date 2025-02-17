// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// The DeleteAuthorizationHandler implementation that uses
/// ClientAuthorizations and NamespaceSecurityElementPaths to
/// interrogate the EdFiDoc and determine whether a Delete operation
/// is authorized and if not, why.
/// </summary>
/// <param name="authorizationStrategyEvaluators"></param>
/// <param name="logger"></param>
public class DeleteAuthorizationHandler(
    AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
    ILogger logger
) : IDeleteAuthorizationHandler
{
    public DeleteAuthorizationResult Authorize(JsonNode securityElements)
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
                    ?? Array.Empty<string>()
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
            var values = authorizationStrategyEvaluators
                .SelectMany(e => e.Filters.Select(f => f.Value))
                .Distinct();

            var errors = andFilterEvaluations
                .Where(e => !e.Value)
                .Select(e =>
                    $"The '{e.Key.FilterPath}' value of the data does not start with any of the caller's associated namespace prefixes ('{string.Join(", ", values)}')."
                );
            return new DeleteAuthorizationResult.NotAuthorizedNamespace(errors.ToArray());
        }

        if (orFilterEvaluations.Any() && orFilterEvaluations.TrueForAll(e => !e.Value))
        {
            var values = authorizationStrategyEvaluators
                .SelectMany(e => e.Filters.Select(f => f.Value))
                .Distinct();

            var errors = orFilterEvaluations
                .Where(e => !e.Value)
                .Select(e =>
                    $"The '{e.Key.FilterPath}' value of the data does not start with any of the caller's associated namespace prefixes ('{string.Join(", ", values)}')."
                );
            return new DeleteAuthorizationResult.NotAuthorizedNamespace(errors.ToArray());
        }

        return new DeleteAuthorizationResult.Authorized();
    }
}
