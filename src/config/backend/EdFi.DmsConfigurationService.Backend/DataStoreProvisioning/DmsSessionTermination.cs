// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

public enum DmsSessionTerminationFailure
{
    /// <summary>The session was still present when the deadline passed.</summary>
    TimedOut,

    /// <summary>The administrative connection may not terminate the session (PostgreSQL <c>42501</c>; SQL Server without <c>ALTER ANY CONNECTION</c>).</summary>
    PrivilegeMissing,

    /// <summary>The server declined without a privilege error (PostgreSQL <c>pg_terminate_backend</c> returned false).</summary>
    Refused,
}

/// <summary>
/// The verdict of <see cref="IDmsTargetEngine.TerminateSessionAsync"/> (D-22). Only the two proven-gone
/// cases permit reading state after an unknown outcome.
/// </summary>
public abstract record DmsSessionTermination
{
    /// <summary>The session was terminated and its absence verified.</summary>
    public sealed record Terminated() : DmsSessionTermination;

    /// <summary>No session with that id and start time exists; it was already gone.</summary>
    public sealed record AlreadyAbsent() : DmsSessionTermination;

    public sealed record NotTerminated(
        DmsSessionTerminationFailure Reason,
        DmsProvisioningDiagnostic? Diagnostic = null
    ) : DmsSessionTermination;
}
