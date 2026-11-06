// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// The strict document reads the trust policy reader and the attestation parser share. Documents are parsed
/// with duplicate member names rejected and without comments or trailing commas, and every member name and
/// string value is decoded once at parse time: <see cref="JsonDocument"/> accepts an unpaired surrogate escape
/// (<c>\uD800</c>) and invalid UTF-8 bytes inside strings and only fails when the text is later read, from
/// <see cref="JsonElement.GetString"/>, from any member lookup in an object with such a name, and even from
/// duplicate-name detection, so <see cref="TryParse(string, out JsonDocument?, out ParseFailure)"/> walks the
/// whole tree and reports <see cref="ParseFailure.MalformedText"/> instead of letting an
/// <see cref="InvalidOperationException"/> escape a later read. A member is read only when it has exactly the
/// expected JSON kind, so a string <c>"1"</c> never passes an integer gate and a number or null never passes a
/// string gate (the delivered <c>Test-TrustJsonInteger</c> rule). Unknown members are ignored.
/// </summary>
internal static partial class TrustContract
{
    private static readonly JsonDocumentOptions _documentOptions = new() { AllowDuplicateProperties = false };

    public enum ParseFailure
    {
        None,

        /// <summary>Not JSON, a duplicate member name, a comment, a trailing comma, or excessive depth.</summary>
        InvalidJson,

        /// <summary>A member name or string value that is not well-formed Unicode: an unpaired surrogate escape, an unpaired surrogate character in the input, or invalid UTF-8 bytes.</summary>
        MalformedText,
    }

    /// <summary>Parses document text; true only when it is valid JSON whose every name and string decodes. The caller disposes the document.</summary>
    public static bool TryParse(
        string json,
        [NotNullWhen(true)] out JsonDocument? document,
        out ParseFailure failure
    )
    {
        ArgumentNullException.ThrowIfNull(json);
        document = null;
        failure = ParseFailure.None;
        try
        {
            document = JsonDocument.Parse(json, _documentOptions);
        }
        catch (JsonException)
        {
            failure = ParseFailure.InvalidJson;
            return false;
        }
        catch (ArgumentException)
        {
            // JsonDocument transcodes the input to UTF-8 with a throwing encoder: an unpaired surrogate
            // character in the input string is rejected here rather than parsed.
            failure = ParseFailure.MalformedText;
            return false;
        }
        catch (InvalidOperationException)
        {
            // Duplicate-name detection decodes member names and fails on an unpaired surrogate escape.
            failure = ParseFailure.MalformedText;
            return false;
        }

        if (ProveWellFormedText(document, out failure))
        {
            return true;
        }

        document = null;
        return false;
    }

    /// <summary>Parses document bytes; true only when they are UTF-8 JSON whose every name and string decodes. The caller disposes the document.</summary>
    public static bool TryParse(
        ReadOnlyMemory<byte> utf8Json,
        [NotNullWhen(true)] out JsonDocument? document,
        out ParseFailure failure
    )
    {
        document = null;
        failure = ParseFailure.None;
        try
        {
            document = JsonDocument.Parse(utf8Json, _documentOptions);
        }
        catch (JsonException)
        {
            failure = ParseFailure.InvalidJson;
            return false;
        }
        catch (InvalidOperationException)
        {
            failure = ParseFailure.MalformedText;
            return false;
        }

        if (ProveWellFormedText(document, out failure))
        {
            return true;
        }

        document = null;
        return false;
    }

    /// <summary>True when the member is a JSON number that is an <see cref="int"/> with no fraction or exponent.</summary>
    public static bool TryGetInteger(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement member)
            && member.ValueKind == JsonValueKind.Number
            && member.TryGetInt32(out value);
    }

    /// <summary>True when the member is a JSON string (empty allowed).</summary>
    public static bool TryGetString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement member)
            && member.ValueKind == JsonValueKind.String
        )
        {
            value = member.GetString();
        }

        return value is not null;
    }

    /// <summary>The canonical hash spelling: exactly 64 lowercase hex characters, nothing before or after.</summary>
    public static bool IsLowercaseSha256Hex(string value) => LowercaseSha256Pattern().IsMatch(value);

    /// <summary>Decodes every name and string once; on a defect, disposes the document and reports <see cref="ParseFailure.MalformedText"/>.</summary>
    private static bool ProveWellFormedText(JsonDocument document, out ParseFailure failure)
    {
        failure = ParseFailure.None;
        try
        {
            DecodeAllText(document.RootElement);
            return true;
        }
        catch (InvalidOperationException)
        {
            document.Dispose();
            failure = ParseFailure.MalformedText;
            return false;
        }
    }

    private static void DecodeAllText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    _ = property.Name;
                    DecodeAllText(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    DecodeAllText(item);
                }

                break;
            case JsonValueKind.String:
                _ = element.GetString();
                break;
        }
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex LowercaseSha256Pattern();
}
