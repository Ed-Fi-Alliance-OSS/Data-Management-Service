// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Produces the state-significant projection of an etag value used for RFC 9110 §13.1.1 If-Match matching.
/// A served etag is "{ContentVersion}-{schemaEpoch}.{format}.{profileCode}.{linkFlag}". Per the ADR
/// (as amended 2026-07-04), If-Match ignores the representation-selector components (format,
/// profileCode, linkFlag) and retains only ContentVersion and schemaEpoch. Two tags match iff their
/// projections are equal (ordinal). Parsing is delegated to <see cref="EtagValue"/> (ContentVersion /
/// variantKey split) and <see cref="VariantKey"/> (variantKey component split).
/// </summary>
/// <remarks>
/// "Malformed" here means <em>structurally</em> malformed — a value <see cref="EtagValue.TryParse"/>
/// rejects (no ContentVersion/variantKey "-" split, or an empty half) or whose variantKey does not
/// split into exactly <see cref="VariantKey.ComponentCount"/> dot-delimited parts. Such a value yields
/// the <see cref="Malformed"/> sentinel, which never equals a well-formed projection.
/// <para>
/// The <em>content</em> of the three ignored positions (format, profileCode, linkFlag) is deliberately
/// NOT validated, so it is not part of well-formedness for matching: once a value's ContentVersion and
/// schemaEpoch equal the current tag's and its variantKey has exactly four parts, it matches whatever
/// the ignored positions hold — empty (e.g. "5-a1b2c3d4...") or an unrecognized code (e.g.
/// "5-a1b2c3d4.x.3.n"). This tolerance is intentional and carries no lost-update risk: a match still
/// requires the correct ContentVersion and schemaEpoch, so a stale or wrong tag never matches, and any
/// client able to supply those could already form a fully well-formed tag. Validating the ignored
/// positions is avoided on purpose — it would re-couple If-Match to the selectors the 2026-07-04
/// amendment decoupled, breaking cross-format / cross-profile / cross-link If-Match.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Composes the state-significant projection directly from persisted state, without first creating
    /// a representation-specific served etag whose format, profile, and link components would immediately
    /// be discarded.
    /// </summary>
    public static string OfCurrentState(long contentVersion, string effectiveSchemaHash) =>
        EtagValue.Compose(
            contentVersion.ToString(CultureInfo.InvariantCulture),
            VariantKeyFactory.SchemaEpoch(effectiveSchemaHash)
        );
}
