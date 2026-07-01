// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
/// <param name="ProfileName">
/// The name of the readable profile in effect. Used as the stable input to the served
/// <c>_etag</c>'s profile discriminator so representations under different profiles carry
/// distinct etags (RFC 7232 strong validators).
/// </param>
public sealed record ReadableProfileProjectionContext(
    ContentTypeDefinition ContentTypeDefinition,
    IReadOnlySet<string> IdentityPropertyNames
)
{
    /// <summary>
    /// The name of the readable profile in effect, used as the stable input to the served
    /// <c>_etag</c>'s profile discriminator. Empty when unspecified by the caller.
    /// </summary>
    public string ProfileName { get; init; } = string.Empty;
}

/// <summary>
/// Relational GET request.
/// </summary>
public interface IGetRequest : IRequestWithMappingSet
{
    /// <summary>
    /// The document UUID to get.
    /// </summary>
    DocumentUuid DocumentUuid { get; }

    /// <summary>
    /// The ResourceName for the resource being retrieved.
    /// </summary>
    ResourceName ResourceName { get; }

    /// <summary>
    /// The request TraceId.
    /// </summary>
    TraceId TraceId { get; }

    /// <summary>
    /// Typed request-scoped authorization inputs for relational single-record planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }

    /// <summary>
    /// The fully qualified/base resource identifier for the request.
    /// This keeps relational read paths independent from the ambiguous bare ResourceName.
    /// </summary>
    BaseResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The effective GET authorization strategies already resolved by Core.
    /// Descriptor GET uses these to fail closed without invoking the legacy authorization handler.
    /// </summary>
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; }

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
