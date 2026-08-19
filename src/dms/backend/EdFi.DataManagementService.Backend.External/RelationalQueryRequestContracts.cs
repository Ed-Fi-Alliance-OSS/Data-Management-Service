// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External;

public static class RelationalAuthorizationParameterNameConstants
{
    public const string ClaimEducationOrganizationIds = nameof(
        RelationalAuthorizationContext.ClaimEducationOrganizationIds
    );
}

/// <summary>
/// Typed request-scoped authorization inputs shared by relational authorization planning and execution.
/// </summary>
public sealed record RelationalAuthorizationContext
{
    public RelationalAuthorizationContext(IReadOnlyList<long> claimEducationOrganizationIds)
        : this(claimEducationOrganizationIds, []) { }

    public RelationalAuthorizationContext(
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> namespacePrefixes
    )
    {
        ArgumentNullException.ThrowIfNull(claimEducationOrganizationIds);
        ArgumentNullException.ThrowIfNull(namespacePrefixes);

        ClaimEducationOrganizationIds = NormalizeClaimEducationOrganizationIds(claimEducationOrganizationIds);
        NamespacePrefixes = NormalizeNamespacePrefixes(namespacePrefixes);
    }

    /// <summary>
    /// Unique token EdOrg claim ids sorted ascending for deterministic planning/binding.
    /// </summary>
    public IReadOnlyList<long> ClaimEducationOrganizationIds { get; }

    /// <summary>
    /// Unique namespace prefixes sorted ordinally for deterministic downstream strategy planning.
    /// </summary>
    public IReadOnlyList<string> NamespacePrefixes { get; }

    public static RelationalAuthorizationContext Create(ClientAuthorizations clientAuthorizations)
    {
        ArgumentNullException.ThrowIfNull(clientAuthorizations);

        return new RelationalAuthorizationContext(
            [
                .. clientAuthorizations.EducationOrganizationIds.Select(static educationOrganizationId =>
                    educationOrganizationId.Value
                ),
            ],
            [
                .. clientAuthorizations.NamespacePrefixes.Select(static namespacePrefix =>
                    namespacePrefix.Value
                ),
            ]
        );
    }

    private static IReadOnlyList<long> NormalizeClaimEducationOrganizationIds(
        IReadOnlyList<long> claimEducationOrganizationIds
    ) => [.. claimEducationOrganizationIds.Distinct().Order()];

    private static IReadOnlyList<string> NormalizeNamespacePrefixes(
        IReadOnlyList<string> namespacePrefixes
    ) => [.. namespacePrefixes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// Relational query request.
/// </summary>
public interface IQueryRequest : IRequestWithMappingSet
{
    /// <summary>
    /// The ResourceInfo for the resource being retrieved.
    /// </summary>
    ResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The elements of this query. This must not include pagination parameters.
    /// </summary>
    QueryElement[] QueryElements { get; }

    /// <summary>
    /// Collection of authorization strategy filters, each specifying collection of filters and filter operator.
    /// </summary>
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; }

    /// <summary>
    /// The paging mode and its inputs for this query: traditional limit/offset, or cursor over an
    /// inclusive DocumentId range.
    /// </summary>
    CollectionPaging Paging { get; }

    /// <summary>
    /// The request TraceId.
    /// </summary>
    TraceId TraceId { get; }

    /// <summary>
    /// The normalized request tenant key used for target-scoped read acceleration.
    /// Empty string identifies the default/non-tenant target.
    /// </summary>
    string TenantKey { get; }

    /// <summary>
    /// Typed request-scoped authorization inputs for relational authorization planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }

    /// <summary>
    /// Optional readable-profile projection inputs for relational query responses.
    /// Null when no readable profile applies to the request.
    /// </summary>
    ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; }

    /// <summary>
    /// The validated minChangeVersion / maxChangeVersion window for this query.
    /// <see cref="ChangeVersionRange.None"/> when neither parameter was supplied.
    /// </summary>
    ChangeVersionRange ChangeVersionRange { get; }

    /// <summary>The content coding selected for the external response.</summary>
    ResponseContentCoding ResponseContentCoding { get; }
}

/// <summary>
/// Relational partition boundary request.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="IQueryRequest" />: the operation selects identifiers and hydrates
/// nothing, so there is nowhere to carry a page, a readable-profile projection, or a response content
/// coding. Every input is typed, and the requested count and minimum size arrive as numbers rather than
/// as anything a provider could parse out of client text — a SQL compiler that could see token text
/// would be one refactor away from making an authorization decision from it.
/// </remarks>
public interface IPartitionRequest : IRequestWithMappingSet
{
    /// <summary>
    /// The ResourceInfo for the resource whose partitions are being calculated.
    /// </summary>
    ResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The elements of the resource-property filter this request was validated with. Boundaries are
    /// calculated over the filtered candidate set, so these are the same elements the equivalent
    /// GET-many request would supply.
    /// </summary>
    QueryElement[] QueryElements { get; }

    /// <summary>
    /// Collection of authorization strategy filters, each specifying collection of filters and filter operator.
    /// </summary>
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; }

    /// <summary>
    /// Typed request-scoped authorization inputs for relational authorization planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }

    /// <summary>
    /// The validated minChangeVersion / maxChangeVersion window for this request.
    /// <see cref="Model.ChangeVersionRange.None"/> when neither parameter was supplied.
    /// </summary>
    ChangeVersionRange ChangeVersionRange { get; }

    /// <summary>
    /// The desired partition count: the client's validated value, or the configured default when the
    /// request omitted it. Core resolves the default, so the backend never has to know one.
    /// </summary>
    int RequestedPartitionCount { get; }

    /// <summary>
    /// The smallest partition, in candidate rows. Long-width because it is derived from the configured
    /// maximum page size in checked 64-bit arithmetic, so a large configured page size cannot wrap it
    /// negative and defeat the minimum-size guard.
    /// </summary>
    long MinimumPartitionSize { get; }

    /// <summary>
    /// The request TraceId.
    /// </summary>
    TraceId TraceId { get; }

    /// <summary>
    /// The normalized request tenant key. Empty string identifies the default/non-tenant target.
    /// </summary>
    string TenantKey { get; }
}
