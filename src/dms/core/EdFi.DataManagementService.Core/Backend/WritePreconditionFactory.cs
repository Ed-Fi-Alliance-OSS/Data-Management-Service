// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.Backend;

internal static class WritePreconditionFactory
{
    private const string IfMatchHeaderName = "If-Match";
    private const string IfNoneMatchHeaderName = "If-None-Match";

    public static WritePrecondition Create(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.TryGetValue(IfMatchHeaderName, out var ifMatchValue))
        {
            // RFC 7232 §3.1 wildcard: a bare (unquoted) "*" is an existence precondition, not an opaque
            // tag. Only the bare form is the wildcard; a quoted "*" flows through as an ordinary
            // (mismatching) tag.
            if (string.Equals(ifMatchValue, "*", StringComparison.Ordinal))
            {
                return new WritePrecondition.IfMatch("*", IsWildcard: true);
            }

            // Normalize the wire form to the opaque tag the backend compares against: strip the
            // surrounding quotes of a strong entity-tag and tolerate a bare unquoted value. A weak (W/)
            // validator is rejected by the helper and kept verbatim so the backend's state-significant
            // projection cannot equal a well-formed current tag (RFC 7232 §3.1: weak validators must not
            // be used with If-Match).
            return EtagValue.TryParseHeaderValue(ifMatchValue, out var opaqueTag)
                ? new WritePrecondition.IfMatch(opaqueTag)
                : new WritePrecondition.IfMatch(ifMatchValue ?? string.Empty);
        }

        if (headers.TryGetValue(IfNoneMatchHeaderName, out var ifNoneMatchValue))
        {
            // RFC 9110 §13.1.2 wildcard: a bare (unquoted) "*" asserts non-existence.
            if (string.Equals(ifNoneMatchValue, "*", StringComparison.Ordinal))
            {
                return new WritePrecondition.IfNoneMatch("*", IsWildcard: true);
            }

            // Weak comparison: accept W/ (unlike If-Match) and tolerate unquoted.
            return EtagValue.TryParseConditionalTag(ifNoneMatchValue, out var opaqueTag)
                ? new WritePrecondition.IfNoneMatch(opaqueTag)
                : new WritePrecondition.IfNoneMatch(ifNoneMatchValue ?? string.Empty);
        }

        return new WritePrecondition.None();
    }
}
