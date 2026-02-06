// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs.ApiSchemaNodeRequirements;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

internal static class DescriptorPathsExtractor
{
    /// <summary>
    /// Resolves descriptor reference paths for the current resource, using the project schema to locate
    /// descriptor mappings and reference-based propagation rules.
    /// </summary>
    /// <param name="resourceSchema">The resource schema for the current resource.</param>
    /// <param name="projectSchema">The project schema containing all resource schemas.</param>
    /// <param name="projectName">The current project name.</param>
    /// <returns>A mapping of canonical JSON path to descriptor path information.</returns>
    internal static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPaths(
        JsonObject resourceSchema,
        JsonObject projectSchema,
        string projectName
    )
    {
        var descriptorPathsByResourceName = DescriptorPathInference.BuildDescriptorPathsByResource(
            new[] { new DescriptorPathInference.ProjectDescriptorSchema(projectName, projectSchema) }
        );
        var resourceName = RequireString(resourceSchema, "resourceName");
        var resourceKey = new QualifiedResourceName(projectName, resourceName);

        if (!descriptorPathsByResourceName.TryGetValue(resourceKey, out var descriptorPaths))
        {
            return new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);
        }

        return new Dictionary<string, DescriptorPathInfo>(descriptorPaths, StringComparer.Ordinal);
    }
}
