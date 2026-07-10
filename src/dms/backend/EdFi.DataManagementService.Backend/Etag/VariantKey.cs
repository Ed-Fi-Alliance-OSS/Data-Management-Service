// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Stable server-side registry of response media types. Codes MUST NOT be derived from a raw
/// media-type string at runtime. JSON is the only format today.
/// </summary>
public enum ResponseFormat
{
    Json,
}

/// <summary>
/// The representation discriminator embedded in a served etag so the tag is a strong validator
/// (RFC 9110 §8.8.1):
/// <c>schemaEpoch "." format "." profileCode "." linkFlag "." contentCoding</c>. All characters are
/// valid <c>etagc</c> (RFC 9110 §8.8.3).
/// </summary>
public readonly record struct VariantKey(string Value)
{
    /// <summary>The <c>profileCode</c> used when no readable profile applies.</summary>
    public const string NoProfileCode = "_";

    /// <summary>Separator between the five variantKey components.</summary>
    public const char ComponentSeparator = '.';

    /// <summary>Number of components: schemaEpoch, format, profileCode, linkFlag, contentCoding.</summary>
    public const int ComponentCount = 5;

    /// <summary>Builds a variantKey wire value from its five components (fixed order).</summary>
    public static VariantKey FromComponents(
        string schemaEpoch,
        string formatCode,
        string profileCode,
        string linkFlag,
        string contentCodingCode
    ) =>
        new(
            string.Join(ComponentSeparator, schemaEpoch, formatCode, profileCode, linkFlag, contentCodingCode)
        );

    /// <summary>
    /// Splits <see cref="Value"/> into its five components. Returns <see langword="false"/> when the
    /// value does not have exactly <see cref="ComponentCount"/> dot-delimited parts. Only the part
    /// <em>count</em> is validated; the individual component values are not (a part may be empty or an
    /// unrecognized code) — the caller decides which components are significant and how to interpret
    /// them (see <see cref="EtagMatchProjection"/>).
    /// </summary>
    public bool TryParseComponents(out Components components)
    {
        components = default;
        if (string.IsNullOrEmpty(Value))
        {
            return false;
        }

        var parts = Value.Split(ComponentSeparator);
        if (parts.Length != ComponentCount)
        {
            return false;
        }

        components = new Components(parts[0], parts[1], parts[2], parts[3], parts[4]);
        return true;
    }

    public override string ToString() => Value;

    /// <summary>The parsed components of a variantKey, in fixed order.</summary>
    public readonly record struct Components(
        string SchemaEpoch,
        string Format,
        string ProfileCode,
        string LinkFlag,
        string ContentCoding
    )
    {
        /// <summary>
        /// The subset that remains significant for RFC 9110 §13.1.1 If-Match comparison. Per the 2026-07-04
        /// ADR amendment this is <see cref="SchemaEpoch"/> only; format, profileCode, linkFlag, and
        /// contentCoding are projected out. This is the single definition of "what If-Match compares".
        /// </summary>
        public string IfMatchSignificant() => SchemaEpoch;
    }
}

/// <summary>
/// The representation inputs a caller supplies so the read materializer can compose a
/// profile/format/link/content-coding-sensitive <c>_etag</c>. <see cref="ProfileName"/> is
/// <see langword="null"/> when no readable profile applies. The materializer supplies the link mode
/// from its own <c>ResourceLinksOptions</c> and the schema epoch from the request's mapping set.
/// </summary>
public readonly record struct EtagVariantInputs(
    string? ProfileName,
    ResponseFormat Format,
    ResponseContentCoding ContentCoding = ResponseContentCoding.Identity
);

/// <summary>Builds a <see cref="VariantKey"/> from a request's representation context.</summary>
public static class VariantKeyFactory
{
    public static VariantKey Create(
        string effectiveSchemaHash,
        ResponseFormat format,
        string profileCode,
        bool linksEnabled,
        ResponseContentCoding contentCoding = ResponseContentCoding.Identity
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(profileCode);

        var schemaEpoch = SchemaEpoch(effectiveSchemaHash);
        var formatCode = FormatCode(format);
        var linkFlag = linksEnabled ? "l" : "n";
        var contentCodingCode = ContentCodingCode(contentCoding);

        return VariantKey.FromComponents(schemaEpoch, formatCode, profileCode, linkFlag, contentCodingCode);
    }

    // First 8 lowercase hex characters of the in-force EffectiveSchemaHash (already lowercase hex,
    // per EffectiveSchemaHashProvider; lowercased defensively).
    internal static string SchemaEpoch(string effectiveSchemaHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(effectiveSchemaHash);
        var lower = effectiveSchemaHash.ToLowerInvariant();
        return lower.Length <= 8 ? lower : lower[..8];
    }

    private static string FormatCode(ResponseFormat format) =>
        format switch
        {
            ResponseFormat.Json => "j",
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "No etag format code registered."
            ),
        };

    private static string ContentCodingCode(ResponseContentCoding contentCoding) =>
        contentCoding switch
        {
            ResponseContentCoding.Identity => "i",
            ResponseContentCoding.Brotli => "b",
            ResponseContentCoding.Gzip => "g",
            _ => throw new ArgumentOutOfRangeException(
                nameof(contentCoding),
                contentCoding,
                "No etag content-coding code registered."
            ),
        };
}
