// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Represents an effective schema project payload and metadata.
/// </summary>
/// <param name="ProjectEndpointName">The stable API endpoint name (e.g., <c>ed-fi</c>).</param>
/// <param name="ProjectName">The logical project name (e.g., <c>Ed-Fi</c>).</param>
/// <param name="ProjectVersion">The project version label.</param>
/// <param name="IsExtensionProject">Whether the project is an extension.</param>
/// <param name="ProjectSchema">The <c>projectSchema</c> node from the normalized ApiSchema payload.</param>
public sealed record EffectiveProjectSchema(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject,
    JsonObject ProjectSchema
);

/// <summary>
/// Represents the normalized multi-project effective schema payload used for set-level derivation.
/// </summary>
/// <param name="EffectiveSchema">The effective schema metadata and resource key seed.</param>
/// <param name="ProjectsInEndpointOrder">Projects ordered by endpoint name.</param>
public sealed record EffectiveSchemaSet(
    EffectiveSchemaInfo EffectiveSchema,
    IReadOnlyList<EffectiveProjectSchema> ProjectsInEndpointOrder
);
