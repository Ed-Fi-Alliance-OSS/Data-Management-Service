// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Result type for API schema file loading operations.
/// Uses discriminated union pattern to represent success or various failure modes.
/// </summary>
public abstract record ApiSchemaFileLoadResult
{
    private ApiSchemaFileLoadResult() { }

    /// <summary>
    /// Loading and normalization succeeded.
    /// </summary>
    public sealed record SuccessResult(ApiSchemaDocumentNodes NormalizedNodes) : ApiSchemaFileLoadResult;

    /// <summary>
    /// A file was not found at the specified path.
    /// </summary>
    public sealed record FileNotFoundResult(string FilePath) : ApiSchemaFileLoadResult;

    /// <summary>
    /// A file could not be read (I/O error, permissions, etc.).
    /// </summary>
    public sealed record FileReadErrorResult(string FilePath, string ErrorMessage) : ApiSchemaFileLoadResult;

    /// <summary>
    /// A file contains invalid JSON.
    /// </summary>
    public sealed record InvalidJsonResult(string FilePath, string ErrorMessage) : ApiSchemaFileLoadResult;

    /// <summary>
    /// Schema normalization failed (wraps the underlying normalization failure).
    /// </summary>
    public sealed record NormalizationFailureResult(ApiSchemaNormalizationResult FailureResult)
        : ApiSchemaFileLoadResult;
}
