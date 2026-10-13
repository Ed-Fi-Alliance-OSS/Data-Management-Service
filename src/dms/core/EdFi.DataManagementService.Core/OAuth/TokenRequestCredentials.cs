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
/// <c>null</c> means the field was absent. An empty string means it was present but empty, which
/// still counts as presence: a Basic header combined with any body credential field is rejected
/// whatever that field holds.
/// </remarks>
public sealed record TokenRequestCredentials(string? ClientId, string? ClientSecret)
{
    /// <summary>
    /// A request body that carried neither field.
    /// </summary>
    public static TokenRequestCredentials None { get; } = new(null, null);

    /// <summary>
    /// Whether the body carried either field, whatever its value.
    /// </summary>
    public bool IsPresent => ClientId is not null || ClientSecret is not null;

    /// <summary>
    /// Never renders the values. The synthesized record <c>ToString</c> would print the client
    /// secret, so a logging call or exception message that captured this record would leak it.
    /// </summary>
    public override string ToString() =>
        $"{nameof(TokenRequestCredentials)} {{ ClientId present = {ClientId is not null}, "
        + $"ClientSecret present = {ClientSecret is not null} }}";
}
