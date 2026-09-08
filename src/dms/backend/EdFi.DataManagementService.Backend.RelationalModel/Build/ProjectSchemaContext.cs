// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Bundles project-level metadata and the normalized project schema payload.
/// </summary>
/// <param name="EffectiveProject">The normalized project schema payload.</param>
/// <param name="ProjectSchema">The project schema metadata with physical schema info.</param>
public sealed record ProjectSchemaContext(
    EffectiveProjectSchema EffectiveProject,
    ProjectSchemaInfo ProjectSchema
);
