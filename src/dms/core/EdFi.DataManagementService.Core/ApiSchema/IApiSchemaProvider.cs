// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Service for managing API schemas including loading, providing, and reloading
/// </summary>
public interface IApiSchemaProvider
{
    /// <summary>
    /// Returns core and extension ApiSchemas
    /// </summary>
    public ApiSchemaDocumentNodes GetApiSchemaNodes();

    /// <summary>
    /// Gets the current reload identifier.
    /// This identifier changes whenever the schema is reloaded.
    /// </summary>
    public Guid ReloadId { get; }

    /// <summary>
    /// Gets whether the currently loaded API schema is valid
    /// </summary>
    public bool IsSchemaValid { get; }

    /// <summary>
    /// Gets the failures from the last schema operation
    /// </summary>
    public List<ApiSchemaFailure> ApiSchemaFailures { get; }

    /// <summary>
    /// Reloads the API schema from the configured source
    /// </summary>
    /// <returns>Success status and any failures that occurred</returns>
    Task<ApiSchemaLoadStatus> ReloadApiSchemaAsync();

    /// <summary>
    /// Loads API schemas from the provided JSON nodes
    /// </summary>
    /// <returns>Success status and any failures that occurred</returns>
    Task<ApiSchemaLoadStatus> LoadApiSchemaFromAsync(JsonNode coreSchema, JsonNode[] extensionSchemas);
}
