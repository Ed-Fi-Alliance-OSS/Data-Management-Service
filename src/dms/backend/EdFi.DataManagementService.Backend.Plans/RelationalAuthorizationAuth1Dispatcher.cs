// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

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

    public sealed record InvalidPayload(string RawPayload) : RelationalAuthorizationAuth1DispatchResult;
}

/// <summary>
/// Routes an AUTH1 provider failure to either the relationship or namespace payload codec based on the
/// payload's leading discriminator. Relationship payloads start with <c>1|</c>; namespace payloads start
/// with <c>ns1|</c>. Any other payload returns <see cref="RelationalAuthorizationAuth1DispatchResult.InvalidPayload"/>
/// so the caller can log and fall through to a generic security failure.
/// </summary>
public static class RelationalAuthorizationAuth1Dispatcher
{
    private const string RelationshipDiscriminatorPrefix =
        RelationshipAuthorizationAuth1FailurePayloadCodec.PayloadVersion + "|";
    private const string NamespaceDiscriminatorPrefix =
        NamespaceAuthorizationAuth1FailurePayloadCodec.PayloadDiscriminator + "|";

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

        if (
            payloadText.StartsWith(RelationshipDiscriminatorPrefix, StringComparison.Ordinal)
            && RelationshipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
                payloadText,
                out var relationshipPayload
            )
            && relationshipPayload is not null
        )
        {
            result = new RelationalAuthorizationAuth1DispatchResult.Relationship(relationshipPayload);
            return true;
        }

        if (
            payloadText.StartsWith(NamespaceDiscriminatorPrefix, StringComparison.Ordinal)
            && NamespaceAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
                payloadText,
                out var namespacePayload
            )
            && namespacePayload is not null
        )
        {
            result = new RelationalAuthorizationAuth1DispatchResult.Namespace(namespacePayload);
            return true;
        }

        result = new RelationalAuthorizationAuth1DispatchResult.InvalidPayload(payloadText);
        return true;
    }

    private static bool TryExtractAuth1Payload(
        SqlDialect dialect,
        string? providerErrorCode,
        string providerMessage,
        out string payloadText
    )
    {
        // Both codecs share the same transport extraction logic (PG SqlState == "AUTH1", or MSSQL
        // message containing "AUTH1 - "). Use the relationship codec's extractor as the single source
        // of truth so a future change to either codec cannot silently diverge from the other.
        return RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            dialect,
            providerErrorCode,
            providerMessage,
            out payloadText
        );
    }
}
