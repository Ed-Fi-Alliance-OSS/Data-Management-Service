// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Logging;
using static EdFi.DmsConfigurationService.DataModel.LoggingUtility;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// The self-contained un-approval participant. It takes the application row lock a token store
/// waits on, then revokes the client's tokens, both in the caller's transaction: a grant that stored
/// a token before the lock is caught by the revocation, and a grant blocked behind it re-validates
/// the approval after the caller commits and is refused.
/// </summary>
public sealed class OpenIddictUnapprovalTokenRevocation(
    IOpenIddictDataRepository dataRepository,
    ILogger<OpenIddictUnapprovalTokenRevocation> logger
) : IUnapprovalTokenRevocation
{
    public async Task<int> RevokeOutstandingTokensAsync(
        Guid clientUuid,
        IDbConnection connection,
        IDbTransaction transaction
    )
    {
        bool applicationExists = await dataRepository.LockApplicationAsync(
            clientUuid,
            connection,
            transaction
        );

        // Token rows have no foreign key to the application, so tokens can outlive it. They are
        // revoked whether or not the application row is still there.
        int revokedCount = await dataRepository.RevokeTokensForApplicationAsync(
            clientUuid,
            connection,
            transaction
        );

        // Both messages describe changes still pending in the caller's transaction: the caller
        // commits or rolls back after this returns, and a rollback preserves each token's previous
        // state (an expired token stays unusable).
        if (!applicationExists)
        {
            logger.LogInformation(
                "No identity provider application exists for client {ClientUuid}; its stored tokens are marked revoked in the pending un-approval transaction regardless",
                SanitizeForLog(clientUuid.ToString())
            );
        }

        logger.LogInformation(
            "Marked {Count} outstanding tokens for client {ClientUuid} revoked in the pending un-approval transaction; a rollback preserves their previous state",
            revokedCount,
            SanitizeForLog(clientUuid.ToString())
        );
        return revokedCount;
    }
}
