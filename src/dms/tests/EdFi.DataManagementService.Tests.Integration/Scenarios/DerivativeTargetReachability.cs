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

    /// <summary>
    /// A connection string that is present and non-blank but that this engine's provider refuses
    /// outright, so acquisition fails before any server is contacted.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AbsentDatabaseConnectionString" />, which is perfectly well-formed text
    /// that fails at the open. A target is selectable on the strength of its configured text alone -
    /// selection deliberately does no provider parsing - so a value like this one must be classified at
    /// the acquisition boundary rather than escape as an unhandled argument failure. It also exercises
    /// the half of each seam guard that a wrap around the open call alone would miss.
    ///
    /// Each engine supplies the refusal its own provider actually raises, which is not the same kind on
    /// both: SQL Server rejects an unsupported keyword while parsing, while PostgreSQL's reachable
    /// refusal is a validation failure during data-source construction, raised after parsing has
    /// already accepted every keyword. Both must be classified, so each implementation names the shape
    /// that would regress if its engine's arm of the classifier were dropped.
    /// </remarks>
    string ProviderInvalidConnectionString(string leasedConnectionString);
}
