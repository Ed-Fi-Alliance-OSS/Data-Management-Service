// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// The result of attempting to store an access token for a client.
/// </summary>
public enum TokenStoreOutcome
{
    /// <summary>
    /// The token row was written.
    /// </summary>
    Stored,

    /// <summary>
    /// Nothing was written because the client already holds at least the maximum number of
    /// active tokens allowed by <c>maxActiveTokens</c>.
    /// </summary>
    LimitExceeded,

    /// <summary>
    /// Nothing was written because the client's application row no longer existed when the
    /// store was attempted, so the client was deleted mid-request.
    /// </summary>
    ClientNotFound,

    /// <summary>
    /// Nothing was written because a database lock wait during the store ran out, or, on SQL
    /// Server, because the store was chosen as a deadlock victim. Usually the wait is behind
    /// another grant holding this client's application row, but on SQL Server the active-token
    /// count can also wait behind, or deadlock with, another writer on the token table, such as
    /// the expired-token sweep. Contention, not a limit rejection: the client may have been
    /// nowhere near its limit, and retrying is the right answer.
    /// </summary>
    LockTimeout,
}
