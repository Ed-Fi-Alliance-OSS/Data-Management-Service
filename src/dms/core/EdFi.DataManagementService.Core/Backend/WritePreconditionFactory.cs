// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// Builds the <see cref="WritePrecondition" /> a write request (POST, PUT, DELETE) carries, from its
/// request headers.
/// </summary>
/// <remarks>
/// DMS-1576: <c>If-None-Match</c> is a conditional-read (GET-only) validator for DMS, matching the
/// ODS/API, which ignores it on every write and honors it only on GET. <see cref="Create" /> therefore
/// never reads it: a write request that carries only <c>If-None-Match</c> (a wildcard, a single tag, or
/// a list) produces <see cref="WritePrecondition.None" />, the same as a request with no conditional
/// header at all. The GET path reads <c>If-None-Match</c> itself (see
/// <c>GetByIdHandler.TryCreateNotModified</c>) and does not go through this factory.
/// </remarks>
internal static class WritePreconditionFactory
{
    private const string IfMatchHeaderName = "If-Match";

    /// <summary>
    /// The conditional-read header name. <see cref="Create" /> never reads it; GET reads it independently.
    /// </summary>
    internal const string IfNoneMatchHeaderName = "If-None-Match";

    /// <summary>
    /// True when a write request carries an <c>If-None-Match</c> header that <see cref="Create" /> ignores.
    /// </summary>
    public static bool IsIfNoneMatchIgnored(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return headers.ContainsKey(IfNoneMatchHeaderName);
    }

    public static WritePrecondition Create(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.TryGetValue(IfMatchHeaderName, out var ifMatchValue))
        {
            // RFC 9110 §13.1.1 wildcard: a bare (unquoted) "*" is an existence precondition, not an opaque
            // tag. Only the bare form is the wildcard; a quoted "*" flows through as an ordinary
            // (mismatching) tag.
            if (string.Equals(ifMatchValue, "*", StringComparison.Ordinal))
            {
                return new WritePrecondition.IfMatch("*", IsWildcard: true);
            }

            // Normalize the wire form to the opaque tag the backend compares against: strip the
            // surrounding quotes of a strong entity-tag and tolerate a bare unquoted value. A weak (W/)
            // validator is rejected by the helper and kept verbatim so the backend's state-significant
            // projection cannot equal a well-formed current tag (RFC 9110 §13.1.1: weak validators must not
            // be used with If-Match).
            return EtagValue.TryParseHeaderValue(ifMatchValue, out var opaqueTag)
                ? new WritePrecondition.IfMatch(opaqueTag)
                : new WritePrecondition.IfMatch(ifMatchValue ?? string.Empty);
        }

        return new WritePrecondition.None();
    }
}
