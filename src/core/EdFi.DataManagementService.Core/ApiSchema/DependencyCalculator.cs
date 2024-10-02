// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema;

internal class DependencyCalculator(JsonNode _apiSchemaRootNode, ILogger _logger)
{
    public JsonArray GetDependenciesFromResourceSchema()
    {
        var apiSchemaDocument = new ApiSchemaDocument(_apiSchemaRootNode, _logger);
        var dependenciesJsonArray = new JsonArray();
        foreach (JsonNode projectSchemaNode in apiSchemaDocument.GetAllProjectSchemaNodes())
        {
            var resourceSchemas = projectSchemaNode["resourceSchemas"]?.AsObject().Select(x => new ResourceSchema(x.Value!)).ToList()!;

            Dictionary<string, List<string>> resources =
                resourceSchemas
                    .ToDictionary(
                        rs => rs.ResourceName.Value,
                        rs => rs.DocumentPaths.Where(d => d.IsReference).Select(d => ReplaceAbstractResourceNames(d.ResourceName.Value)).ToList());

            var orderedResources = GetDependencies(resources);

            string ResourceNameMapping(string resourceName)
            {
                var resourceNameNode = projectSchemaNode["resourceNameMapping"];
                if (resourceNameNode == null)
                {
                    throw new InvalidOperationException("ResourceNameMapping missing");
                }

                if (resourceName.EndsWith("Descriptor"))
                {
                    resourceName = resourceName.Replace("Descriptor", string.Empty);
                }

                var resourceNode = resourceNameNode[resourceName];
                if (resourceNode == null)
                {
                    throw new InvalidOperationException($"No resource name mapping for {resourceName}");
                }

                return resourceNode.GetValue<string>();
            }

            foreach (var orderedResource in orderedResources.OrderBy(o => o.Value).ThenBy(o => o.Key))
            {
                string resourceName = ResourceNameMapping(orderedResource.Key);

                dependenciesJsonArray.Add(new { resource = $"/{projectSchemaNode!.GetPropertyName()}/{resourceName}", order = orderedResource.Value, operations = new[] { "Create", "Update" } });
            }
        }

        return dependenciesJsonArray;
    }

    private static string ReplaceAbstractResourceNames(string resourceName)
    {
        // These resources are similar to abstract base classes, so they are not represented in the resourceSchemas
        // portion of the schema document. This is a rudimentary replacement with the most specific version of the resource
        if (resourceName == "EducationOrganization")
        {
            resourceName = "School";
        }

        if (resourceName == "GeneralStudentProgramAssociation")
        {
            resourceName = "StudentProgramAssociation";
        }

        return resourceName;
    }

    public static Dictionary<string, int> GetDependencies(Dictionary<string, List<string>> resources)
    {
        Dictionary<string, int> orderedNodes = resources.ToDictionary(d => d.Key, _ => 0);
        Dictionary<string, int> visitedNodes = [];

        foreach (var resource in resources.Select(d => d.Key))
        {
            RecursivelyDetermineDependencies(resource);
        }

        int RecursivelyDetermineDependencies(string resourceName)
        {
            if (orderedNodes[resourceName] > 0)
            {
                return orderedNodes[resourceName];
            }

            visitedNodes.TryAdd(resourceName, 0);
            var maxDepth = 0;
            foreach (var dependency in resources[resourceName])
            {
                if (visitedNodes.ContainsKey(dependency))
                {
                    if (visitedNodes[dependency] > maxDepth)
                    {
                        maxDepth = visitedNodes[dependency];
                    }
                }
                else
                {
                    var level = RecursivelyDetermineDependencies(dependency);
                    if (level > maxDepth)
                    {
                        maxDepth = level;
                    }
                }
            }
            orderedNodes[resourceName] = maxDepth + 1;
            visitedNodes[resourceName] = maxDepth + 1;
            return maxDepth + 1;
        }

        return orderedNodes;
    }
}
