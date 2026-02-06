// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs.ApiSchemaNodeRequirements;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

/// <summary>
/// Extracts and validates compiled identity JSONPaths for a resource schema.
/// </summary>
internal static class IdentityJsonPathsExtractor
{
    /// <summary>
    /// Compiles identity JSON path strings defined by a resource schema into canonical
    /// <see cref="JsonPathExpression"/> instances.
    /// </summary>
    /// <param name="resourceSchema">The resource schema containing <c>identityJsonPaths</c>.</param>
    /// <returns>The compiled identity paths.</returns>
    internal static IReadOnlyList<JsonPathExpression> ExtractIdentityJsonPaths(
        JsonObject resourceSchema,
        string projectName,
        string resourceName
    )
    {
        var identityJsonPaths = RequireArray(resourceSchema, "identityJsonPaths");
        List<JsonPathExpression> compiledPaths = new(identityJsonPaths.Count);
        HashSet<string> seenPaths = new(StringComparer.Ordinal);
        HashSet<string> duplicatePaths = new(StringComparer.Ordinal);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            if (identityJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected identityJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            var identityPath = identityJsonPath.GetValue<string>();
            var compiledPath = JsonPathExpressionCompiler.Compile(identityPath);
            compiledPaths.Add(compiledPath);

            if (!seenPaths.Add(compiledPath.Canonical))
            {
                duplicatePaths.Add(compiledPath.Canonical);
            }
        }

        if (duplicatePaths.Count > 0)
        {
            var duplicates = string.Join(", ", duplicatePaths.OrderBy(path => path, StringComparer.Ordinal));

            throw new InvalidOperationException(
                $"identityJsonPaths on resource '{projectName}:{resourceName}' contains duplicate JSONPaths: {duplicates}."
            );
        }

        return compiledPaths.ToArray();
    }
}
