// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

/// <summary>
/// Runs inside the Configuration Service transaction that persists an API client's un-approval,
/// before the <c>IsApproved</c> column is written. It serializes with token issuance and revokes the
/// client's outstanding identity-provider tokens, so the un-approval commits atomically with their
/// revocation: if either fails, the caller's transaction rolls both back.
/// </summary>
public interface IUnapprovalTokenRevocation
{
    /// <summary>
    /// Revokes every outstanding token of the identity-provider client <paramref name="clientUuid"/>
    /// inside the caller's transaction. Failures propagate to the caller.
    /// </summary>
    /// <returns>The number of tokens revoked.</returns>
    Task<int> RevokeOutstandingTokensAsync(
        Guid clientUuid,
        IDbConnection connection,
        IDbTransaction transaction
    );
}
