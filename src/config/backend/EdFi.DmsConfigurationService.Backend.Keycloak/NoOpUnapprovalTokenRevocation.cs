// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DmsConfigurationService.Backend.Repositories;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// The Keycloak un-approval participant. The Configuration Service stores no Keycloak token, so
/// there is nothing to revoke here; disabling the client at Keycloak, done by the provider update,
/// does not reach tokens that are validated locally.
/// </summary>
public sealed class NoOpUnapprovalTokenRevocation : IUnapprovalTokenRevocation
{
    public Task<int> RevokeOutstandingTokensAsync(
        Guid clientUuid,
        IDbConnection connection,
        IDbTransaction transaction
    ) => Task.FromResult(0);
}
