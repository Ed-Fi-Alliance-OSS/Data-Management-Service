// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Runtime failure kinds encoded in the compact AUTH1 namespace-authorization payload.
/// </summary>
/// <remarks>
/// The §2.9 "no namespace prefixes configured" case is emitted at planner/preflight time and does not
/// reach the AUTH1 channel, so it has no encoding here.
/// </remarks>
public enum NamespaceAuthorizationAuth1FailureKind
{
    /// <summary>The namespace value does not start with any configured prefix.</summary>
    NamespaceMismatch,

    /// <summary>The stored namespace value is null or empty.</summary>
    StoredNamespaceUninitialized,

    /// <summary>The proposed namespace value is null or empty.</summary>
    ProposedNamespaceMissing,
}

/// <summary>
/// Provider-independent AUTH1 failure payload for one failed namespace authorization check.
/// </summary>
public sealed record NamespaceAuthorizationAuth1FailurePayload
{
    public NamespaceAuthorizationAuth1FailurePayload(
        int emittedAuth1Index,
        NamespaceAuthorizationAuth1FailureKind failureKind
    )
    {
        if (emittedAuth1Index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(emittedAuth1Index),
                emittedAuth1Index,
                "Emitted AUTH1 index cannot be negative."
            );
        }

        EmittedAuth1Index = emittedAuth1Index;
        FailureKind = failureKind;
    }

    public int EmittedAuth1Index { get; init; }

    public NamespaceAuthorizationAuth1FailureKind FailureKind { get; init; }
}

/// <summary>
/// Encodes, extracts, and parses the compact AUTH1 namespace-authorization payload (<c>ns1|index|kind</c>).
/// </summary>
/// <remarks>
/// The payload shares the AUTH1 SqlState / message-prefix transport with the relationship-authorization
/// codec, but carries a distinct <c>ns1</c> discriminator so a dispatcher can route each payload to the
/// correct codec without modifying the relationship v1 payload.
/// </remarks>
public static class NamespaceAuthorizationAuth1FailurePayloadCodec
{
    public const string ProviderFailureCode = "AUTH1";
    public const string PayloadDiscriminator = "ns1";

    private const string MssqlPayloadMarker = "AUTH1 - ";

    public static string Encode(NamespaceAuthorizationAuth1FailurePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PayloadDiscriminator}|{payload.EmittedAuth1Index}|{EncodeFailureKind(payload.FailureKind)}"
        );
    }

    public static bool TryParsePayload(
        string payloadText,
        out NamespaceAuthorizationAuth1FailurePayload? payload
    )
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(payloadText))
        {
            return false;
        }

        var payloadSections = payloadText.Split('|');

        if (
            payloadSections.Length is not 3
            || !string.Equals(payloadSections[0], PayloadDiscriminator, StringComparison.Ordinal)
            || !TryParseNonNegativeInt(payloadSections[1], out var emittedAuth1Index)
            || !TryDecodeFailureKind(payloadSections[2], out var failureKind)
        )
        {
            return false;
        }

        payload = new NamespaceAuthorizationAuth1FailurePayload(emittedAuth1Index, failureKind);
        return true;
    }

    public static bool TryParseProviderFailure(
        SqlDialect dialect,
        string? providerErrorCode,
        string providerMessage,
        out NamespaceAuthorizationAuth1FailurePayload? payload
    )
    {
        if (!TryExtractProviderPayload(dialect, providerErrorCode, providerMessage, out var payloadText))
        {
            payload = null;
            return false;
        }

        return TryParsePayload(payloadText, out payload);
    }

    public static bool TryExtractProviderPayload(
        SqlDialect dialect,
        string? providerErrorCode,
        string providerMessage,
        out string payloadText
    )
    {
        payloadText = string.Empty;

        if (string.IsNullOrWhiteSpace(providerMessage))
        {
            return false;
        }

        return dialect switch
        {
            SqlDialect.Pgsql => TryExtractPostgresqlPayload(
                providerErrorCode,
                providerMessage,
                out payloadText
            ),
            SqlDialect.Mssql => TryExtractMssqlPayload(providerMessage, out payloadText),
            _ => false,
        };
    }

    private static bool TryParseNonNegativeInt(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string EncodeFailureKind(NamespaceAuthorizationAuth1FailureKind failureKind) =>
        failureKind switch
        {
            NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch => "m",
            NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized => "u",
            NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing => "r",
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 namespace failure kind."
            ),
        };

    private static bool TryDecodeFailureKind(
        string failureKindCode,
        out NamespaceAuthorizationAuth1FailureKind failureKind
    )
    {
        switch (failureKindCode)
        {
            case "m":
                failureKind = NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch;
                return true;
            case "u":
                failureKind = NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized;
                return true;
            case "r":
                failureKind = NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing;
                return true;
            default:
                failureKind = NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch;
                return false;
        }
    }

    private static bool TryExtractPostgresqlPayload(
        string? providerErrorCode,
        string providerMessage,
        out string payloadText
    )
    {
        if (!string.Equals(providerErrorCode, ProviderFailureCode, StringComparison.Ordinal))
        {
            payloadText = string.Empty;
            return false;
        }

        payloadText = providerMessage;
        return true;
    }

    private static bool TryExtractMssqlPayload(string providerMessage, out string payloadText)
    {
        payloadText = string.Empty;
        var markerIndex = providerMessage.IndexOf(MssqlPayloadMarker, StringComparison.Ordinal);

        if (markerIndex < 0)
        {
            return false;
        }

        var payloadStartIndex = markerIndex + MssqlPayloadMarker.Length;
        var payloadEndIndex = payloadStartIndex;

        while (
            payloadEndIndex < providerMessage.Length
            && IsMssqlPayloadCharacter(providerMessage[payloadEndIndex])
        )
        {
            payloadEndIndex++;
        }

        if (payloadEndIndex == payloadStartIndex)
        {
            return false;
        }

        payloadText = providerMessage[payloadStartIndex..payloadEndIndex];
        return true;
    }

    private static bool IsMssqlPayloadCharacter(char value) =>
        value is >= '0' and <= '9' || value is >= 'a' and <= 'z' || value is '|' or ':' or ',';
}
