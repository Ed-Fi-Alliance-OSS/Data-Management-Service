// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
/// <param name="clientAuthorizations"></param>
/// <param name="namespaceSecurityElementPaths"></param>
/// <param name="logger"></param>
public class DeleteAuthorizationHandler(
    AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
    ILogger logger
) : IDeleteAuthorizationHandler
{
    public DeleteAuthorizationResult Authorize(JsonNode edFiDoc)
    {
        foreach (var evaluator in authorizationStrategyEvaluators)
        {
            List<KeyValuePair<string, bool>> filterEvaluations = [];
            foreach (var filter in evaluator.Filters)
            {
                string valueFromDocument = edFiDoc.SelectRequiredNodeFromPathCoerceToString(
                    filter.FilterPath.Value,
                    logger
                );
                switch (filter.Comparison)
                {
                    case FilterComparison.Equals:
                        filterEvaluations.Add(new KeyValuePair<string, bool>(filter.FilterPath.Value, valueFromDocument.Equals(filter.Value)));
                        break;
                    case FilterComparison.StartsWith:
                        filterEvaluations.Add(new KeyValuePair<string, bool>(filter.FilterPath.Value, valueFromDocument.StartsWith(filter.Value)));
                        break;
                }
            }

            if (evaluator.Operator == FilterOperator.And && !filterEvaluations.TrueForAll(b => b.Value))
            {
                var errors = filterEvaluations.Where(e => !e.Value).Select(e =>
                    $"The '{e.Key}' value of the data does not start with any of the caller's associated namespace prefixes ('{string.Join(", ", evaluator.Filters.Select(f => f.Value))}').");
                return new DeleteAuthorizationResult.NotAuthorizedNamespace(errors.ToArray());
            }

            if (evaluator.Operator == FilterOperator.Or && !filterEvaluations.Exists(b => b.Value))
            {
                var errors = filterEvaluations.Where(e => !e.Value).Select(e =>
                    $"The '{e.Key}' value of the data does not start with any of the caller's associated namespace prefixes ('{string.Join(", ", evaluator.Filters.Select(f => f.Value))}').");
                return new DeleteAuthorizationResult.NotAuthorizedNamespace(errors.ToArray());
            }
        }

        return new DeleteAuthorizationResult.Authorized();
    }
}
