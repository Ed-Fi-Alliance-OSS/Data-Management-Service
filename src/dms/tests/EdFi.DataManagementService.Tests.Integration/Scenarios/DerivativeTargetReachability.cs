// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Makes one leased database deliberately unreachable, and puts it back.
/// </summary>
/// <remarks>
/// This is how a routing assertion becomes non-vacuous without adding a production seam. Three cloned
/// databases have identical fingerprints, identical resource-key seeds, and identical schema, so a
/// response body alone only proves which database the repository query and hydration read. If every
/// database except the selected one is unreachable and the request still succeeds, then no part of the
/// request - fingerprint read, resource-key read, authorization SQL, repository query, or hydration -
/// touched anything else, because touching an unreachable database cannot succeed quietly.
///
/// The connection string is never changed. Reachability is switched at the server, so the identity the
/// configuration names, and therefore the pool it realizes to, is the same before and after.
/// </remarks>
public interface IDerivativeTargetReachability
{
    /// <summary>Refuses new connections to the database this connection string names.</summary>
    Task MakeUnreachableAsync(string leasedConnectionString);

    /// <summary>Restores connections to the database this connection string names.</summary>
    Task MakeReachableAsync(string leasedConnectionString);

    /// <summary>
    /// A connection string that names a database which does not exist, for a target that must never be
    /// opened at all. Unlike an unreachable database this needs no cleanup.
    /// </summary>
    string AbsentDatabaseConnectionString(string leasedConnectionString);
}
