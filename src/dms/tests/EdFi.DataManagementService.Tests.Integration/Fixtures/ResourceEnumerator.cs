// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

/// <summary>
/// Walks each ApiSchema file's <c>projectSchema.resourceSchemas</c> and returns
/// distinct <c>(projectName, resourceName)</c> pairs. Consumed by the fake
/// claim-set provider so it can grant CRUD on every resource the fixture exposes.
/// </summary>
internal static class ResourceEnumerator
{
    public static IReadOnlyList<QualifiedResourceName> FromApiSchemaFiles(
        IReadOnlyList<string> apiSchemaFilePaths
    )
    {
        var seen = new HashSet<QualifiedResourceName>();
        var resources = new List<QualifiedResourceName>();

        foreach (string filePath in apiSchemaFilePaths)
        {
            string json = File.ReadAllText(filePath);
            JsonNode root =
                JsonNode.Parse(json)
                ?? throw new InvalidOperationException(
                    $"ApiSchema file '{filePath}' parsed to a null JSON document."
                );

            JsonObject projectSchema =
                root["projectSchema"]?.AsObject()
                ?? throw new InvalidOperationException(
                    $"ApiSchema file '{filePath}' is missing 'projectSchema'."
                );

            string projectName =
                projectSchema["projectName"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"ApiSchema file '{filePath}' is missing 'projectSchema.projectName'."
                );

            JsonObject? resourceSchemas = projectSchema["resourceSchemas"]?.AsObject();
            if (resourceSchemas is null)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> entry in resourceSchemas)
            {
                JsonObject? resourceSchema = entry.Value?.AsObject();
                if (resourceSchema is null)
                {
                    continue;
                }

                string? resourceName = resourceSchema["resourceName"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(resourceName))
                {
                    continue;
                }

                var qualified = new QualifiedResourceName(projectName, resourceName);
                if (seen.Add(qualified))
                {
                    resources.Add(qualified);
                }
            }
        }

        return resources;
    }
}
