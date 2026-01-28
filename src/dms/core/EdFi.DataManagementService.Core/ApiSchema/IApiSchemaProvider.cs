// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Service for loading and providing API schemas.
/// Schema loading occurs once at startup; runtime reload is not supported.
/// </summary>
public interface IApiSchemaProvider
{
    /// <summary>
    /// Returns core and extension ApiSchemas.
    /// This triggers schema loading on first access if not already loaded.
    /// </summary>
    ApiSchemaDocumentNodes GetApiSchemaNodes();

    /// <summary>
    /// Gets the unique identifier for the loaded schema.
    /// This value is stable for the lifetime of the process.
    /// </summary>
    Guid ReloadId { get; }

    /// <summary>
    /// Gets whether the currently loaded API schema is valid.
    /// </summary>
    bool IsSchemaValid { get; }

    /// <summary>
    /// Gets the failures from the schema loading operation.
    /// </summary>
    List<ApiSchemaFailure> ApiSchemaFailures { get; }
}
