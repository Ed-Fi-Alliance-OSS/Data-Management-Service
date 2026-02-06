// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Schema;

/// <summary>
/// The normalized project schema contexts and public schema infos derived from an effective schema set.
/// </summary>
internal sealed record ProjectSchemaNormalizationResult(
    IReadOnlyList<ProjectSchemaContext> ProjectSchemas,
    IReadOnlyList<ProjectSchemaInfo> ProjectSchemaInfos
);

/// <summary>
/// Normalizes projects from an <see cref="EffectiveSchemaSet"/> into canonical endpoint/schema ordering and
/// validates physical schema uniqueness.
/// </summary>
internal sealed class ProjectSchemaNormalizer
{
    /// <summary>
    /// Normalizes project schemas and returns per-project contexts and schema infos.
    /// </summary>
    /// <param name="effectiveSchemaSet">The effective schema set to normalize.</param>
    public ProjectSchemaNormalizationResult Normalize(EffectiveSchemaSet effectiveSchemaSet)
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

        var projectsInEndpointOrder = NormalizeProjectsInEndpointOrder(effectiveSchemaSet);
        return BuildProjectSchemaContexts(projectsInEndpointOrder);
    }

    /// <summary>
    /// Validates and canonicalizes <see cref="EffectiveSchemaSet.ProjectsInEndpointOrder"/> into a stable
    /// endpoint-name sort order.
    /// </summary>
    private static IReadOnlyList<EffectiveProjectSchema> NormalizeProjectsInEndpointOrder(
        EffectiveSchemaSet effectiveSchemaSet
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        if (effectiveSchemaSet.ProjectsInEndpointOrder.Count == 0)
        {
            return Array.Empty<EffectiveProjectSchema>();
        }

        if (effectiveSchemaSet.ProjectsInEndpointOrder.Any(project => project is null))
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
            );
        }

        var orderedProjects = effectiveSchemaSet
            .ProjectsInEndpointOrder.OrderBy(project => project.ProjectEndpointName, StringComparer.Ordinal)
            .ToArray();

        return orderedProjects;
    }

    /// <summary>
    /// Builds normalized project schema contexts and schema info objects for all projects.
    /// </summary>
    private static ProjectSchemaNormalizationResult BuildProjectSchemaContexts(
        IReadOnlyList<EffectiveProjectSchema> projectsInEndpointOrder
    )
    {
        List<ProjectSchemaContext> projectSchemas = new(projectsInEndpointOrder.Count);
        List<ProjectSchemaInfo> projectSchemaInfos = new(projectsInEndpointOrder.Count);
        Dictionary<string, ProjectSchemaInfo> physicalSchemasByName = new(StringComparer.Ordinal);

        foreach (var project in projectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectEndpointName = RequireNonEmpty(project.ProjectEndpointName, "ProjectEndpointName");
            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var projectVersion = RequireNonEmpty(project.ProjectVersion, "ProjectVersion");
            var physicalSchema = RelationalNameConventions.NormalizeSchemaName(projectEndpointName);

            if (physicalSchemasByName.TryGetValue(physicalSchema.Value, out var existing))
            {
                throw new InvalidOperationException(
                    $"Project endpoint '{projectEndpointName}' normalizes to physical schema '{physicalSchema.Value}', "
                        + $"which is already used by project endpoint '{existing.ProjectEndpointName}'."
                );
            }

            var schemaInfo = new ProjectSchemaInfo(
                projectEndpointName,
                projectName,
                projectVersion,
                project.IsExtensionProject,
                physicalSchema
            );

            physicalSchemasByName.Add(physicalSchema.Value, schemaInfo);
            projectSchemaInfos.Add(schemaInfo);
            projectSchemas.Add(new ProjectSchemaContext(project, schemaInfo));
        }

        return new ProjectSchemaNormalizationResult(projectSchemas.ToArray(), projectSchemaInfos.ToArray());
    }
}
