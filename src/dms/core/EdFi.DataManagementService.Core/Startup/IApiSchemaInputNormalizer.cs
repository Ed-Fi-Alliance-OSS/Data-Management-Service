// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Normalizes API schema inputs to a deterministic canonical form.
/// This ensures that the same logical schema always produces the same
/// EffectiveSchemaHash regardless of formatting differences.
/// </summary>
public interface IApiSchemaInputNormalizer
{
    /// <summary>
    /// Normalizes the provided API schema nodes to a canonical form.
    /// </summary>
    /// <param name="nodes">The raw API schema nodes to normalize.</param>
    /// <returns>The normalized API schema nodes.</returns>
    ApiSchemaDocumentNodes Normalize(ApiSchemaDocumentNodes nodes);
}
