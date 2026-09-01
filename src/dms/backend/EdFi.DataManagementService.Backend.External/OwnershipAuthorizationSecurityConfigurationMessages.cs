// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Single source for the ownership-authorization security-configuration (HTTP 500) messages, mirroring
/// <see cref="NamespaceAuthorizationSecurityConfigurationMessages"/> so both the relational backend and
/// Core's ProblemDetails formatter use identical wording.
/// </summary>
/// <remarks>
/// No message discloses an ownership token value. A count is safe to report — it tells an operator what to
/// fix — but the tokens themselves identify other clients' data partitions.
/// </remarks>
public static class OwnershipAuthorizationSecurityConfigurationMessages
{
    /// <summary>
    /// The ownership AUTH1 failure payload returned by the authorization provider (the <c>own1|index|kind</c>
    /// metadata) cannot be attributed to the request's planned ownership check — either the request planned
    /// none, or the payload's configured strategy index is not the planned check's. Fails closed as a
    /// security-configuration (HTTP 500) rather than as a 403 attributed to a strategy that did not deny the
    /// request.
    /// </summary>
    public const string InvalidAuthorizationMetadata =
        "The ownership authorization failure payload returned by the authorization provider is invalid and cannot be mapped to the configured ownership authorization plan.";

    /// <summary>
    /// The client's ownership-token list reaches the defensive limit for <c>OwnershipBased</c> authorization.
    /// Provider-independent: the same limit applies on PostgreSQL and SQL Server.
    /// </summary>
    public static string TokenCapExceeded(int ownershipTokenCount) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "The API client has {0} ownership tokens, which reaches the supported limit for OwnershipBased authorization. Configure fewer than 2,000 ownership tokens.",
            ownershipTokenCount
        );
}
