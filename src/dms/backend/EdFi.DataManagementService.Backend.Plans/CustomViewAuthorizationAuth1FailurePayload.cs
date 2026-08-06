// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Runtime failure kinds encoded in the compact AUTH1 custom view-based authorization payload.
/// </summary>
/// <remarks>
/// Stored-value checks emit <see cref="NoMatchingCustomViewRow"/>, <see cref="StoredBasisValueNull"/>, and
/// <see cref="StoredTargetMissing"/>; proposed-value checks emit <see cref="NoMatchingCustomViewRow"/> and
/// <see cref="ProposedBasisValueMissing"/>. Neither value source emits the other's uninitialized kind, which
/// mirrors how the namespace codec splits its stored and proposed kinds.
/// <para>
/// A missing or non-conforming <c>auth.{StrategyName}</c> view is not encoded here: it is not an
/// authorization decision but a security-configuration problem, and it surfaces as the
/// <c>urn:ed-fi:api:system</c> 500 rather than through this channel.
/// </para>
/// </remarks>
public enum CustomViewAuthorizationAuth1FailureKind
{
    /// <summary>
    /// The basis resource's DocumentId resolved, but it is not present in the custom authorization view.
    /// Maps to the auth.md §2.4 403 (authorization denied without EdOrg-claims wording).
    /// </summary>
    NoMatchingCustomViewRow,

    /// <summary>
    /// The stored basis value is null somewhere along the resolved path, so no basis DocumentId exists to
    /// authorize. Only reachable when the basis maps to a nullable/non-PK column. Maps to auth.md §2.7.
    /// </summary>
    StoredBasisValueNull,

    /// <summary>
    /// The proposed basis value is absent, so the request body supplies no basis DocumentId to authorize.
    /// Maps to auth.md §2.8.
    /// </summary>
    ProposedBasisValueMissing,

    /// <summary>
    /// The stored target row no longer exists. The row was deleted between the unlocked target lookup and
    /// the stored check, so the check has nothing to authorize. Read paths re-resolve the target and
    /// surface the resulting 404; locked write and delete paths row-lock the target before the check runs,
    /// so they never reach this branch.
    /// </summary>
    StoredTargetMissing,
}

/// <summary>
/// Provider-independent AUTH1 failure payload for one failed custom view-based authorization check.
/// </summary>
public sealed record CustomViewAuthorizationAuth1FailurePayload
{
    public CustomViewAuthorizationAuth1FailurePayload(
        int emittedAuth1Index,
        CustomViewAuthorizationAuth1FailureKind failureKind
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

    public CustomViewAuthorizationAuth1FailureKind FailureKind { get; init; }
}

/// <summary>
/// Encodes, extracts, and parses the compact AUTH1 custom view-based authorization payload
/// (<c>cv1|index|kind</c>).
/// </summary>
/// <remarks>
/// The payload shares the AUTH1 SqlState / message-prefix transport with the relationship and namespace
/// codecs, but carries a distinct <c>cv1</c> discriminator. Because each codec owns its own discriminator,
/// each also owns an independent index space: a custom-view index can never be mistaken for a namespace or
/// relationship index, so the three families need no shared counter.
/// </remarks>
public static class CustomViewAuthorizationAuth1FailurePayloadCodec
{
    public const string ProviderFailureCode = "AUTH1";
    public const string PayloadDiscriminator = "cv1";

    public static string Encode(CustomViewAuthorizationAuth1FailurePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PayloadDiscriminator}|{payload.EmittedAuth1Index}|{EncodeFailureKind(payload.FailureKind)}"
        );
    }

    public static bool TryParsePayload(
        string payloadText,
        out CustomViewAuthorizationAuth1FailurePayload? payload
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

        payload = new CustomViewAuthorizationAuth1FailurePayload(emittedAuth1Index, failureKind);
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

    private static string EncodeFailureKind(CustomViewAuthorizationAuth1FailureKind failureKind) =>
        failureKind switch
        {
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow => "n",
            CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull => "u",
            CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing => "r",
            CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing => "s",
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 custom view failure kind."
            ),
        };

    private static bool TryDecodeFailureKind(
        string failureKindCode,
        out CustomViewAuthorizationAuth1FailureKind failureKind
    )
    {
        switch (failureKindCode)
        {
            case "n":
                failureKind = CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow;
                return true;
            case "u":
                failureKind = CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull;
                return true;
            case "r":
                failureKind = CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing;
                return true;
            case "s":
                failureKind = CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing;
                return true;
            default:
                failureKind = CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow;
                return false;
        }
    }
}
