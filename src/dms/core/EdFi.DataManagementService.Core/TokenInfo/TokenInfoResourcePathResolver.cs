// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.TokenInfo;

internal static class TokenInfoResourcePathResolver
{
    public static bool TryGetEndpointName(
        ApiSchemaDocuments apiSchemaDocuments,
        string projectEndpointName,
        string resourceName,
        out string endpointName
    )
    {
        endpointName = string.Empty;

        if (string.IsNullOrWhiteSpace(projectEndpointName) || string.IsNullOrWhiteSpace(resourceName))
        {
            return false;
        }

        var projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new ProjectEndpointName(projectEndpointName)
        );

        if (projectSchema is null)
        {
            return false;
        }

        JsonNode? resourceSchemaNode = projectSchema
            .GetAllResourceSchemaNodes()
            .Find(node =>
            {
                var nodeResourceName = node?["resourceName"]?.GetValue<string>();
                return !string.IsNullOrEmpty(nodeResourceName)
                    && string.Equals(nodeResourceName, resourceName, StringComparison.OrdinalIgnoreCase);
            });

        if (resourceSchemaNode is null)
        {
            return false;
        }

        var normalizedResourceName = resourceSchemaNode["resourceName"]?.GetValue<string>() ?? resourceName;

        var resolvedEndpoint = projectSchema.GetEndpointNameFromResourceName(
            new ResourceName(normalizedResourceName)
        );

        endpointName = resolvedEndpoint.Value;
        return true;
    }
}
