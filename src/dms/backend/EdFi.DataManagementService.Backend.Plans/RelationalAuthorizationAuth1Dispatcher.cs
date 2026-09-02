// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// The AUTH1 payload families, one per leading discriminator the dispatcher knows.
/// </summary>
/// <remarks>
/// The dispatcher is the single owner of the discriminator-to-family mapping. Callers that need to know
/// which family a payload announced itself as — including one that announced a family whose codec could not
/// then parse it — read it off the dispatch result rather than re-testing the raw prefix, so a family added
/// here cannot be missed by a caller that never learned about it.
/// </remarks>
public enum RelationalAuthorizationAuth1PayloadFamily
{
    Relationship,
    Namespace,
    CustomView,
    Ownership,
}

/// <summary>
/// Result of dispatching an AUTH1 provider failure to the codec that owns its payload shape.
/// </summary>
public abstract record RelationalAuthorizationAuth1DispatchResult
{
    private RelationalAuthorizationAuth1DispatchResult() { }

    public sealed record Relationship(RelationshipAuthorizationAuth1FailurePayload Payload)
        : RelationalAuthorizationAuth1DispatchResult;

    public sealed record Namespace(NamespaceAuthorizationAuth1FailurePayload Payload)
        : RelationalAuthorizationAuth1DispatchResult;

    public sealed record CustomView(CustomViewAuthorizationAuth1FailurePayload Payload)
        : RelationalAuthorizationAuth1DispatchResult;

    public sealed record Ownership(OwnershipAuthorizationAuth1FailurePayload Payload)
        : RelationalAuthorizationAuth1DispatchResult;

    /// <summary>
    /// The payload could not be decoded into any family's shape.
    /// </summary>
    /// <param name="RawPayload">The undecodable payload text, as extracted from the provider failure.</param>
    /// <param name="RecognizedFamily">
    /// The family whose discriminator the payload leads with, or <see langword="null"/> when it leads with no
    /// known discriminator at all. A non-null value says the payload announced itself as that family's even
    /// though that family's codec could not parse it, which is what lets a mapper claim its own malformed
    /// payload and decline another family's without re-testing the prefix itself.
    /// </param>
    public sealed record InvalidPayload(
        string RawPayload,
        RelationalAuthorizationAuth1PayloadFamily? RecognizedFamily
    ) : RelationalAuthorizationAuth1DispatchResult;
}

/// <summary>
/// Routes an AUTH1 provider failure to the relationship, namespace, custom-view, or ownership payload codec
/// based on the payload's leading discriminator. Relationship payloads start with <c>1|</c>; namespace
/// payloads start with <c>ns1|</c>; custom view-based payloads start with <c>cv1|</c>; ownership payloads
/// start with <c>own1|</c>. Any other payload returns
/// <see cref="RelationalAuthorizationAuth1DispatchResult.InvalidPayload"/> so the caller can log and fall
/// through to a generic security failure.
/// </summary>
/// <remarks>
/// The discriminators are mutually exclusive as anchored prefixes, so arm order carries no meaning: notably
/// <c>own1|…</c> does not start with the relationship family's <c>1|</c>, because a prefix match is anchored
/// at the start of the payload.
/// </remarks>
public static class RelationalAuthorizationAuth1Dispatcher
{
    private const string RelationshipDiscriminatorPrefix =
        RelationshipAuthorizationAuth1FailurePayloadCodec.PayloadVersion + "|";
    private const string NamespaceDiscriminatorPrefix =
        NamespaceAuthorizationAuth1FailurePayloadCodec.PayloadDiscriminator + "|";
    private const string CustomViewDiscriminatorPrefix =
        CustomViewAuthorizationAuth1FailurePayloadCodec.PayloadDiscriminator + "|";
    private const string OwnershipDiscriminatorPrefix =
        OwnershipAuthorizationAuth1FailurePayloadCodec.PayloadDiscriminator + "|";

    /// <summary>
    /// Attempts to extract and dispatch an AUTH1 payload from a provider exception.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the provider failure carries an AUTH1 payload (regardless of whether
    /// the payload decoded successfully); <see langword="false"/> when no AUTH1 payload is present.
    /// </returns>
    public static bool TryDispatch(
        SqlDialect dialect,
        string? providerErrorCode,
        string providerMessage,
        out RelationalAuthorizationAuth1DispatchResult? result
    )
    {
        result = null;

        if (
            !TryExtractAuth1Payload(dialect, providerErrorCode, providerMessage, out var payloadText)
            || string.IsNullOrWhiteSpace(payloadText)
        )
        {
            return false;
        }

        var recognizedFamily = RecognizeFamily(payloadText);

        switch (recognizedFamily)
        {
            case RelationalAuthorizationAuth1PayloadFamily.Relationship
                when RelationshipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
                    payloadText,
                    out var relationshipPayload
                ) && relationshipPayload is not null:
                result = new RelationalAuthorizationAuth1DispatchResult.Relationship(relationshipPayload);
                return true;

            case RelationalAuthorizationAuth1PayloadFamily.Namespace
                when NamespaceAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
                    payloadText,
                    out var namespacePayload
                ) && namespacePayload is not null:
                result = new RelationalAuthorizationAuth1DispatchResult.Namespace(namespacePayload);
                return true;

            case RelationalAuthorizationAuth1PayloadFamily.CustomView
                when CustomViewAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
                    payloadText,
                    out var customViewPayload
                ) && customViewPayload is not null:
                result = new RelationalAuthorizationAuth1DispatchResult.CustomView(customViewPayload);
                return true;

            case RelationalAuthorizationAuth1PayloadFamily.Ownership
                when OwnershipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
                    payloadText,
                    out var ownershipPayload
                ) && ownershipPayload is not null:
                result = new RelationalAuthorizationAuth1DispatchResult.Ownership(ownershipPayload);
                return true;

            default:
                // Either no known discriminator, or a known one whose codec could not parse the rest. The
                // recognized family rides along either way, so a mapper can tell its own malformed payload
                // from another family's without re-testing the prefix.
                result = new RelationalAuthorizationAuth1DispatchResult.InvalidPayload(
                    payloadText,
                    recognizedFamily
                );
                return true;
        }
    }

    /// <summary>
    /// The AUTH1 family whose discriminator <paramref name="payloadText"/> leads with, or
    /// <see langword="null"/> when it leads with none of them.
    /// </summary>
    /// <remarks>
    /// The single place any code decides which family a raw payload belongs to. Exposed so a mapper holding
    /// an already-extracted payload can answer the same question without repeating the prefix constants; a
    /// mapper holding a dispatch result reads
    /// <see cref="RelationalAuthorizationAuth1DispatchResult.InvalidPayload.RecognizedFamily"/> instead.
    /// <para>
    /// The discriminators are mutually exclusive as anchored prefixes, so the arm order carries no meaning —
    /// notably <c>own1|…</c> does not start with the relationship family's <c>1|</c>.
    /// </para>
    /// </remarks>
    public static RelationalAuthorizationAuth1PayloadFamily? RecognizeFamily(string payloadText)
    {
        ArgumentNullException.ThrowIfNull(payloadText);

        if (payloadText.StartsWith(RelationshipDiscriminatorPrefix, StringComparison.Ordinal))
        {
            return RelationalAuthorizationAuth1PayloadFamily.Relationship;
        }

        if (payloadText.StartsWith(NamespaceDiscriminatorPrefix, StringComparison.Ordinal))
        {
            return RelationalAuthorizationAuth1PayloadFamily.Namespace;
        }

        if (payloadText.StartsWith(CustomViewDiscriminatorPrefix, StringComparison.Ordinal))
        {
            return RelationalAuthorizationAuth1PayloadFamily.CustomView;
        }

        if (payloadText.StartsWith(OwnershipDiscriminatorPrefix, StringComparison.Ordinal))
        {
            return RelationalAuthorizationAuth1PayloadFamily.Ownership;
        }

        return null;
    }

    private static bool TryExtractAuth1Payload(
        SqlDialect dialect,
        string? providerErrorCode,
        string providerMessage,
        out string payloadText
    )
    {
        // Every codec shares the same transport extraction logic (PG SqlState == "AUTH1", or MSSQL
        // message containing "AUTH1 - "). Use the relationship codec's extractor as the single source
        // of truth so a future change to any one codec cannot silently diverge from the others.
        return RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            dialect,
            providerErrorCode,
            providerMessage,
            out payloadText
        );
    }
}
