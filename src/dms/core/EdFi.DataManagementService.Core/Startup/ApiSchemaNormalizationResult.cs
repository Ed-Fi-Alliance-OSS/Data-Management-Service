// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Result type for API schema normalization operations.
/// Uses discriminated union pattern to represent success or various failure modes.
/// </summary>
public abstract record ApiSchemaNormalizationResult
{
    private ApiSchemaNormalizationResult() { }

    /// <summary>
    /// Normalization succeeded with the provided normalized nodes.
    /// </summary>
    public sealed record SuccessResult(ApiSchemaDocumentNodes NormalizedNodes) : ApiSchemaNormalizationResult;

    /// <summary>
    /// A schema is missing the projectSchema node or has malformed structure.
    /// </summary>
    public sealed record MissingOrMalformedProjectSchemaResult(string SchemaSource, string Details)
        : ApiSchemaNormalizationResult;

    /// <summary>
    /// Extension schema has a different apiSchemaVersion than the core schema.
    /// </summary>
    public sealed record ApiSchemaVersionMismatchResult(
        string ExpectedVersion,
        string ActualVersion,
        string SchemaSource
    ) : ApiSchemaNormalizationResult;

    /// <summary>
    /// Multiple schemas have the same projectEndpointName, which must be unique.
    /// Reports all collisions found, not just the first one.
    /// </summary>
    public sealed record ProjectEndpointNameCollisionResult(IReadOnlyList<EndpointNameCollision> Collisions)
        : ApiSchemaNormalizationResult;

    /// <summary>
    /// Represents a single projectEndpointName collision.
    /// </summary>
    public sealed record EndpointNameCollision(string ProjectEndpointName, string[] ConflictingSources);
}
