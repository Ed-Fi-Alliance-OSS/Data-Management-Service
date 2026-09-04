// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Runtime failure kinds encoded in the compact AUTH1 ownership-authorization payload.
/// </summary>
/// <remarks>
/// There is no "no ownership tokens configured" kind. A caller holding an empty ownership-token list still
/// executes the stored-row check, so that the response can distinguish a stored null from a non-matching
/// stored value rather than guessing before the row is read. There is likewise no cap-exceeded kind: the
/// defensive token limit is a planner terminal reported before any statement is emitted.
/// </remarks>
public enum OwnershipAuthorizationAuth1FailureKind
{
    /// <summary>
    /// The stored <c>CreatedByOwnershipTokenId</c> is non-null and matches none of the caller's tokens.
    /// </summary>
    OwnershipTokenMismatch,

    /// <summary>The stored <c>CreatedByOwnershipTokenId</c> is null.</summary>
    StoredOwnershipTokenUninitialized,

    /// <summary>
    /// The stored target row no longer exists. The row was deleted between the unlocked target lookup and
    /// the ownership check, so the check has nothing to authorize. Read paths re-resolve the target and
    /// surface the resulting 404; locked write/delete paths never observe this because the row is row-locked
    /// before the check runs.
    /// </summary>
    StoredTargetMissing,
}

/// <summary>
/// Provider-independent AUTH1 failure payload for the failed ownership authorization check.
/// </summary>
/// <remarks>
/// Carries the <em>configured</em> strategy index rather than an emitted statement ordinal. Ownership emits
/// exactly one check per operation, so an emitted ordinal would be a constant zero carrying no information,
/// while the configured index identifies which configured strategy denied the request — which is what the
/// AUTH1 design asks the index to do. The namespace and custom view-based codecs carry emitted ordinals
/// because they emit several checks per request that share one provider exception.
/// </remarks>
public sealed record OwnershipAuthorizationAuth1FailurePayload
{
    public OwnershipAuthorizationAuth1FailurePayload(
        int configuredStrategyIndex,
        OwnershipAuthorizationAuth1FailureKind failureKind
    )
    {
        if (configuredStrategyIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredStrategyIndex),
                configuredStrategyIndex,
                "Configured strategy index cannot be negative."
            );
        }

        ConfiguredStrategyIndex = configuredStrategyIndex;
        FailureKind = failureKind;
    }

    /// <summary>
    /// Zero-based position of <c>OwnershipBased</c> in the CMS-configured strategy list for this request.
    /// </summary>
    public int ConfiguredStrategyIndex { get; init; }

    public OwnershipAuthorizationAuth1FailureKind FailureKind { get; init; }
}

/// <summary>
/// Encodes and parses the compact AUTH1 ownership-authorization payload (<c>own1|configuredIndex|kind</c>).
/// </summary>
/// <remarks>
/// The payload shares the AUTH1 SqlState / message-prefix transport with the relationship, namespace and
/// custom view-based codecs, but carries a distinct <c>own1</c> discriminator so a dispatcher can route each
/// payload to the correct codec without any existing payload shape changing.
/// </remarks>
public static class OwnershipAuthorizationAuth1FailurePayloadCodec
{
    public const string ProviderFailureCode = "AUTH1";
    public const string PayloadDiscriminator = "own1";

    public static string Encode(OwnershipAuthorizationAuth1FailurePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PayloadDiscriminator}|{payload.ConfiguredStrategyIndex}|{EncodeFailureKind(payload.FailureKind)}"
        );
    }

    public static bool TryParsePayload(
        string payloadText,
        out OwnershipAuthorizationAuth1FailurePayload? payload
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
            || !TryParseNonNegativeInt(payloadSections[1], out var configuredStrategyIndex)
            || !TryDecodeFailureKind(payloadSections[2], out var failureKind)
        )
        {
            return false;
        }

        payload = new OwnershipAuthorizationAuth1FailurePayload(configuredStrategyIndex, failureKind);
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

    private static string EncodeFailureKind(OwnershipAuthorizationAuth1FailureKind failureKind) =>
        failureKind switch
        {
            OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch => "m",
            OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized => "u",
            OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing => "s",
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 ownership failure kind."
            ),
        };

    private static bool TryDecodeFailureKind(
        string failureKindCode,
        out OwnershipAuthorizationAuth1FailureKind failureKind
    )
    {
        switch (failureKindCode)
        {
            case "m":
                failureKind = OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch;
                return true;
            case "u":
                failureKind = OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized;
                return true;
            case "s":
                failureKind = OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing;
                return true;
            default:
                failureKind = OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch;
                return false;
        }
    }
}
