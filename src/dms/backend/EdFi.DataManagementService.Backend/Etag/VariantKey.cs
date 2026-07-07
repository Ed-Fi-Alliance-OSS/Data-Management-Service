// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
/// (RFC 7232 §2.1): <c>schemaEpoch "." format "." profileCode "." linkFlag</c>. All characters
/// are valid <c>etagc</c> (RFC 7232 §2.3).
/// </summary>
public readonly record struct VariantKey(string Value)
{
    /// <summary>The <c>profileCode</c> used when no readable profile applies.</summary>
    public const string NoProfileCode = "_";

    /// <summary>Separator between the four variantKey components.</summary>
    public const char ComponentSeparator = '.';

    /// <summary>Number of components: schemaEpoch, format, profileCode, linkFlag.</summary>
    public const int ComponentCount = 4;

    /// <summary>Builds a variantKey wire value from its four components (fixed order).</summary>
    public static VariantKey Format(
        string schemaEpoch,
        string formatCode,
        string profileCode,
        string linkFlag
    ) => new(string.Join(ComponentSeparator, schemaEpoch, formatCode, profileCode, linkFlag));

    /// <summary>
    /// Splits <see cref="Value"/> into its four components. Returns <see langword="false"/> when the
    /// value does not have exactly <see cref="ComponentCount"/> dot-delimited parts.
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

        components = new Components(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    public override string ToString() => Value;

    /// <summary>The parsed components of a variantKey, in fixed order.</summary>
    // The nested Format property intentionally shares its name with the enclosing type's static
    // builder method; they are unrelated members, and the shared name mirrors the wire grammar term
    // used throughout this file's documentation.
#pragma warning disable S3218 // Format property does not shadow the outer type's static Format method.
    public readonly record struct Components(
        string SchemaEpoch,
        string Format,
        string ProfileCode,
        string LinkFlag
    )
    {
#pragma warning restore S3218
        /// <summary>
        /// The subset that remains significant for RFC 7232 If-Match comparison. Per the 2026-07-04
        /// ADR amendment this is <see cref="SchemaEpoch"/> only; format, profileCode, and linkFlag are
        /// projected out. This is the single definition of "what If-Match compares".
        /// </summary>
        public string IfMatchSignificant() => SchemaEpoch;
    }
}

/// <summary>
/// The representation inputs a caller supplies so the read materializer can compose a
/// profile/format/link-sensitive <c>_etag</c>. <see cref="ProfileName"/> is <see langword="null"/>
/// when no readable profile applies. The materializer supplies the link mode from its own
/// <c>ResourceLinksOptions</c> and the schema epoch from the request's mapping set.
/// </summary>
public readonly record struct EtagVariantInputs(string? ProfileName, ResponseFormat Format);

/// <summary>Builds a <see cref="VariantKey"/> from a request's representation context.</summary>
public static class VariantKeyFactory
{
    public static VariantKey Create(
        string effectiveSchemaHash,
        ResponseFormat format,
        string profileCode,
        bool linksEnabled
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(effectiveSchemaHash);
        ArgumentException.ThrowIfNullOrEmpty(profileCode);

        var schemaEpoch = SchemaEpoch(effectiveSchemaHash);
        var formatCode = FormatCode(format);
        var linkFlag = linksEnabled ? "l" : "n";

        return VariantKey.Format(schemaEpoch, formatCode, profileCode, linkFlag);
    }

    // First 8 lowercase hex characters of the in-force EffectiveSchemaHash (already lowercase hex,
    // per EffectiveSchemaHashProvider; lowercased defensively).
    private static string SchemaEpoch(string effectiveSchemaHash)
    {
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
}
