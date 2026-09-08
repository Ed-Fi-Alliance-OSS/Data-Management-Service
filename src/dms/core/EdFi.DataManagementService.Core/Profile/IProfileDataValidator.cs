// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Validates profile definitions against the API schema to ensure they reference valid resources and members.
/// </summary>
internal interface IProfileDataValidator
{
    /// <summary>
    /// Validates a profile definition against the API schema.
    /// </summary>
    /// <param name="profileDefinition">The parsed profile definition to validate.</param>
    /// <param name="effectiveApiSchemaProvider">Provider for accessing the effective API schema documents.</param>
    /// <returns>A validation result containing any errors or warnings found.</returns>
    ProfileValidationResult Validate(
        ProfileDefinition profileDefinition,
        IEffectiveApiSchemaProvider effectiveApiSchemaProvider
    );
}
