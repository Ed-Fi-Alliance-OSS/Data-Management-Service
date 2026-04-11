// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Selects how a relational GET result should be materialized locally.
/// </summary>
public enum RelationalGetRequestReadMode
{
    /// <summary>
    /// Materialize a public API response, including API metadata and optional readable projection.
    /// </summary>
    ExternalResponse,

    /// <summary>
    /// Materialize the stored document shape for internal read-modify-write flows.
    /// </summary>
    StoredDocument,
}

/// <summary>
/// Minimal readable-profile inputs needed by the relational read path after full reconstitution.
/// </summary>
/// <param name="ContentTypeDefinition">The readable profile content-type definition.</param>
/// <param name="IdentityPropertyNames">
/// The precomputed top-level identity-property names that must always survive projection.
/// </param>
public sealed record ReadableProfileProjectionContext(
    ContentTypeDefinition ContentTypeDefinition,
    IReadOnlySet<string> IdentityPropertyNames
);

/// <summary>
/// Backend-local relational GET request.
/// </summary>
public interface IRelationalGetRequest : IGetRequest
{
    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// Supported relational middleware-owned execution paths populate this before repository
    /// execution; null remains possible only for direct-call or pipeline-bypass scenarios.
    /// </summary>
    MappingSet? MappingSet { get; }

    /// <summary>
    /// The fully qualified/base resource identifier for the request.
    /// This keeps relational read paths independent from the ambiguous bare ResourceName.
    /// </summary>
    BaseResourceInfo ResourceInfo { get; }

    /// <summary>
    /// Controls whether the repository should materialize an external response or a stored document.
    /// </summary>
    RelationalGetRequestReadMode ReadMode { get; }

    /// <summary>
    /// Optional readable-profile projection inputs for external response reads.
    /// Null when no readable profile applies or projection must be suppressed.
    /// </summary>
    ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; }
}
