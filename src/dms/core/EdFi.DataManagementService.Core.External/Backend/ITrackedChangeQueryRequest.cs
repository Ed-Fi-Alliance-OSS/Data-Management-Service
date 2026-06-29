// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

public interface ITrackedChangeQueryRequest
{
    ResourceInfo ResourceInfo { get; }

    ChangeQueryEndpointOperation Operation { get; }

    PaginationParameters PaginationParameters { get; }

    ChangeVersionRange ChangeVersionRange { get; }

    TraceId TraceId { get; }
}

/// <summary>
/// A request-level (preflight) ReadChanges authorization failure for Change Query endpoints.
/// </summary>
public abstract record ChangeQueryAuthorizationFailure
{
    private ChangeQueryAuthorizationFailure() { }

    /// <summary>
    /// 500 — one or more configured strategies have no ReadChanges implementation, or the backend
    /// detected a concrete security-configuration error while planning the request.
    /// </summary>
    public sealed record SecurityConfiguration(
        IReadOnlyList<string> UnavailableStrategyNames,
        IReadOnlyList<string> Errors
    ) : ChangeQueryAuthorizationFailure
    {
        public SecurityConfiguration(IReadOnlyList<string> UnavailableStrategyNames)
            : this(UnavailableStrategyNames, []) { }
    }

    /// <summary>403 — NamespaceBased is configured but the client has no namespace prefixes.</summary>
    public sealed record NamespaceNoPrefixesConfigured(string StrategyName) : ChangeQueryAuthorizationFailure;
}

public sealed record TrackedChangeQueryResult(
    JsonArray Items,
    long? TotalCount,
    ChangeQueryAuthorizationFailure? AuthorizationFailure = null
);
