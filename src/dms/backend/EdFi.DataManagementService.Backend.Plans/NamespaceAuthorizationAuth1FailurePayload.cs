// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

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

    /// <summary>
    /// The stored target row no longer exists. The row was deleted between the unlocked target lookup
    /// and the stored namespace check, so the check has nothing to authorize. Read paths re-resolve the
    /// target and surface the resulting 404; locked write/delete paths never observe this because the
    /// row is row-locked before the check runs.
    /// </summary>
    StoredTargetMissing,
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
            NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing => "s",
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
            case "s":
                failureKind = NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing;
                return true;
            default:
                failureKind = NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch;
                return false;
        }
    }
}
