// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Response model for API schema upload operations
/// </summary>
/// <param name="Success">Indicates if the upload was successful</param>
/// <param name="ErrorMessage">Error message if upload failed</param>
/// <param name="SchemasProcessed">Number of schemas processed (core + extensions)</param>
/// <param name="ReloadId">Unique identifier for this reload operation</param>
/// <param name="IsManagementEndpointsDisabled">Indicates if the failure was due to management endpoints being disabled</param>
/// <param name="IsValidationError">Indicates if the failure was due to validation errors (invalid JSON, missing required fields, etc.)</param>
/// <param name="Failures">List of all API schema failures that occurred during the upload</param>
public record UploadSchemaResponse(
    bool Success,
    string? ErrorMessage = null,
    int SchemasProcessed = 0,
    Guid? ReloadId = null,
    bool IsManagementEndpointsDisabled = false,
    bool IsValidationError = false,
    List<ApiSchemaFailure>? Failures = null
);
