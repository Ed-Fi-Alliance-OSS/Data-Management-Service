// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Produces the state-significant projection of an etag value used for RFC 7232 If-Match matching.
/// A served etag is "{ContentVersion}-{schemaEpoch}.{format}.{profileCode}.{linkFlag}". Per the ADR
/// (as amended 2026-07-04), If-Match ignores the representation-selector components (format,
/// profileCode, linkFlag) and retains only ContentVersion and schemaEpoch. Two tags match iff their
/// projections are equal (ordinal). A malformed tag yields a sentinel that cannot equal any
/// well-formed projection. Parsing is delegated to <see cref="EtagValue"/> (ContentVersion / variantKey
/// split) and <see cref="VariantKey"/> (variantKey component split).
/// </summary>
public static class EtagMatchProjection
{
    // This sentinel lacks the '-' / '.' structure of a well-formed projection, so it never matches a real tag.
    private const string Malformed = "!malformed";

    public static string Of(string? etagValue)
    {
        if (!EtagValue.TryParse(etagValue, out var contentVersion, out var variantKeyValue))
        {
            return Malformed;
        }

        if (!new VariantKey(variantKeyValue).TryParseComponents(out var components))
        {
            return Malformed;
        }

        // The projection is "{ContentVersion}-{schemaEpoch}", which is exactly EtagValue.Compose of the
        // ContentVersion with the state-significant component. Format/profileCode/linkFlag are dropped.
        return EtagValue.Compose(contentVersion, components.IfMatchSignificant());
    }
}
