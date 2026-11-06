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
/// with duplicate member names rejected and without comments or trailing commas; a member is read only when it
/// has exactly the expected JSON kind, so a string <c>"1"</c> never passes an integer gate and a number or null
/// never passes a string gate (the delivered <c>Test-TrustJsonInteger</c> rule). Unknown members are ignored.
/// </summary>
internal static partial class TrustContract
{
    public static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

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

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex LowercaseSha256Pattern();
}
