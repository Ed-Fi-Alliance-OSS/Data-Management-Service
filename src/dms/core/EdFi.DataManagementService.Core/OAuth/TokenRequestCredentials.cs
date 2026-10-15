// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.OAuth;

/// <summary>
/// The client credentials a token request carried in its body, as the frontend read them from a
/// form or JSON payload.
/// </summary>
/// <remarks>
/// <c>null</c> means the field was absent. An empty value is stored as <c>null</c> too, because
/// RFC 6749 section 3.2 says parameters sent without a value must be treated as if they were
/// omitted, so every content type gets that rule here. A whitespace-only value is a value and is
/// kept as sent.
/// </remarks>
public sealed record TokenRequestCredentials(string? ClientId, string? ClientSecret)
{
    /// <summary>
    /// The body <c>client_id</c>, or <c>null</c> when it was absent or empty.
    /// </summary>
    public string? ClientId { get; } = NullIfEmpty(ClientId);

    /// <summary>
    /// The body <c>client_secret</c>, or <c>null</c> when it was absent or empty. Never logged.
    /// </summary>
    public string? ClientSecret { get; } = NullIfEmpty(ClientSecret);

    /// <summary>
    /// A request body that carried neither field.
    /// </summary>
    public static TokenRequestCredentials None { get; } = new(null, null);

    /// <summary>
    /// Whether either field holds a non-blank value, which is what makes the body a second
    /// client authentication mechanism beside a Basic header. A whitespace-only value doesn't
    /// count, matching the Ed-Fi ODS/API's <c>IsNullOrWhiteSpace</c> test.
    /// </summary>
    public bool HasNonBlankField =>
        !string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// Never renders the values. The synthesized record <c>ToString</c> would print the client
    /// secret, so a logging call or exception message that captured this record would leak it.
    /// </summary>
    public override string ToString() =>
        $"{nameof(TokenRequestCredentials)} {{ ClientId present = {ClientId is not null}, "
        + $"ClientSecret present = {ClientSecret is not null} }}";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
