// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Thrown when an API client's ownership-token list reaches the defensive limit for <c>OwnershipBased</c>
/// authorization. The repository layer maps this to a 500 Security Configuration Error so the client can
/// diagnose the configuration limit without seeing an internal SQL error.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="NamespacePrefixLimitExceededException"/>, this limit is <strong>provider-independent</strong>.
/// It applies on PostgreSQL, which binds the whole list as one array parameter and has no engine limit at
/// these sizes, exactly as it applies on SQL Server, which binds one scalar parameter per token. The limit is
/// a defensive bound on the configuration rather than an artifact of one dialect's parameter ceiling — that
/// it also keeps the SQL Server <c>IN</c> chain under the engine's usable per-command parameter count is a
/// convenience, not the reason.
/// </para>
/// <para>
/// This backs up a limit enforced upstream rather than duplicating it.
/// <c>ConfigurationServiceApplicationProvider.MaximumOwnershipTokenCount</c> already rejects a CMS response
/// carrying more than 1,999 tokens, classifying it as malformed and mapping it to a 503 before the values
/// reach <c>RelationalAuthorizationContext</c>. The two agree on the boundary and cannot both fire for one
/// request: this exception is reachable only through an authorization context that bypassed that provider.
/// Keep the two in step — 1,999 must remain a valid configuration that works end to end.
/// </para>
/// </remarks>
public sealed class OwnershipTokenLimitExceededException : InvalidOperationException
{
    /// <summary>
    /// The count at which ownership-token configuration fails closed, on every provider. CMS limits
    /// assignments to one below this value.
    /// </summary>
    public const int OwnershipTokenLimit = 2000;

    public OwnershipTokenLimitExceededException(int ownershipTokenCount)
        : base(BuildMessage(ownershipTokenCount))
    {
        OwnershipTokenCount = ownershipTokenCount;
    }

    public int OwnershipTokenCount { get; }

    private static string BuildMessage(int ownershipTokenCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The API client has {ownershipTokenCount} ownership tokens, which reaches the supported limit of {OwnershipTokenLimit} for OwnershipBased authorization."
        );
}
