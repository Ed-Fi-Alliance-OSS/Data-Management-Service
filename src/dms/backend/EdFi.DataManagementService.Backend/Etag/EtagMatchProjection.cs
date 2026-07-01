// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Produces the state-significant projection of an etag value used for RFC 7232 If-Match matching.
/// A served etag is "{ContentVersion}-{schemaEpoch}.{format}.{profileCode}.{linkFlag}". Per the ADR,
/// If-Match ignores the representation-encoding components (format, linkFlag) and retains
/// ContentVersion, schemaEpoch, and profileCode. Two tags match iff their projections are equal
/// (ordinal). A malformed tag yields a sentinel that cannot equal any well-formed projection.
/// </summary>
public static class EtagMatchProjection
{
    // The space cannot occur in a valid etagc-only projection, so this never matches a real tag.
    private const string Malformed = "!malformed";

    public static string Of(string? etagValue)
    {
        if (string.IsNullOrEmpty(etagValue))
        {
            return Malformed;
        }

        // ContentVersion "-" variantKey: ContentVersion is digits-only and variantKey components are
        // [a-z0-9_], so the first '-' is the unambiguous separator.
        var dash = etagValue.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == etagValue.Length - 1)
        {
            return Malformed;
        }

        var contentVersion = etagValue[..dash];
        var variantKey = etagValue[(dash + 1)..];
        var parts = variantKey.Split('.');
        if (parts.Length != 4)
        {
            return Malformed;
        }

        var schemaEpoch = parts[0];
        var profileCode = parts[2]; // parts[1] = format, parts[3] = linkFlag are intentionally dropped

        return $"{contentVersion}-{schemaEpoch}.{profileCode}";
    }
}
