// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts the security elements for a resource
/// </summary>
internal static class SecurityElementsExtractor
{
    /// <summary>
    /// Takes an API JSON body for the resource and extracts the security elements information from the JSON body.
    /// </summary>
    public static DocumentSecurityElements ExtractSecurityElements(
        this ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
        logger.LogDebug("SecurityElementsExtractor.ExtractSecurityElements");

        // HashSet for uniqueness
        HashSet<string> namespaceSecurityElements = [];
        foreach (JsonPath securityElementPath in resourceSchema.NamespaceSecurityElementPaths)
        {
            // Security JsonPaths can return arrays
            namespaceSecurityElements.UnionWith(
                documentBody.SelectNodesFromArrayPathCoerceToStrings(securityElementPath.Value, logger)
            );
        }

        HashSet<EducationOrganizationSecurityElement> edOrgSecurityElements = [];
        foreach (
            EducationOrganizationSecurityElementPath securityElementPath in resourceSchema.EducationOrganizationSecurityElementPaths
        )
        {
            edOrgSecurityElements.UnionWith(
                documentBody
                    .SelectNodesFromArrayPathCoerceToStrings(securityElementPath.Path.Value, logger)
                    .Select(x => new EducationOrganizationSecurityElement(
                        securityElementPath.ResourceName,
                        new EducationOrganizationId(long.Parse(x))
                    ))
            );
        }

        return new([.. namespaceSecurityElements], [.. edOrgSecurityElements]);
    }
}
