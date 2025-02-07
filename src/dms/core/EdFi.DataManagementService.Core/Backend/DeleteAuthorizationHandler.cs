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
    ClientAuthorizations clientAuthorizations,
    IEnumerable<JsonPath> namespaceSecurityElementPaths,
    ILogger logger
) : IDeleteAuthorizationHandler
{
    public DeleteAuthorizationResult Authorize(JsonNode edFiDoc)
    {
        if (clientAuthorizations.NamespacePrefixes.Any())
        {
            foreach (JsonPath namespacePath in namespaceSecurityElementPaths)
            {
                string namespaceFromDocument = edFiDoc.SelectRequiredNodeFromPathCoerceToString(
                    namespacePath.Value,
                    logger
                );
                if (
                    !clientAuthorizations.NamespacePrefixes.Any(n =>
                        namespaceFromDocument.StartsWith(n.Value, StringComparison.InvariantCultureIgnoreCase)
                    )
                )
                {
                    return new DeleteAuthorizationResult.NotAuthorizedNamespace();
                }
            }
        }

        return new DeleteAuthorizationResult.Authorized();
    }
}
