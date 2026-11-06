// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The class of an error that proves the intended effect did not commit (6.9a definite failure).
/// </summary>
public enum DmsCommandFailureClass
{
    /// <summary>Another database already holds the name (PostgreSQL <c>42P04</c>; SQL Server 1801-class).</summary>
    DuplicateName,

    /// <summary>The database is in use (PostgreSQL <c>55006</c>; SQL Server 5061-class).</summary>
    ObjectInUse,

    PrivilegeMissing,

    /// <summary>The object no longer exists.</summary>
    DoesNotExist,

    /// <summary>
    /// Any other error that proves the intended effect did not commit, such as a syntax or constraint error
    /// returned in the command's own response. Server reachability alone is not that proof: an error that
    /// leaves the commit in doubt is reported as <see cref="DmsCommandOutcome.Unknown"/>, not as a failure.
    /// </summary>
    Rejected,
}

public sealed record DmsCommandFailure(DmsCommandFailureClass Class, DmsProvisioningDiagnostic Diagnostic);

/// <summary>
/// The outcome of a mutating engine command (6.9a). <c>Succeeded</c> and <c>Failed</c> are definite;
/// <c>Unknown</c> means the client cannot tell whether the effect committed (timeout, cancellation after
/// send, connection loss, process killed), and names the session that must be proven terminated before
/// state is read (D-7b, D-22). The session is null only when the engine itself already proved every
/// session of the command gone (a replay process whose server session was looked up by its application
/// name and terminated with verification, 6.3 step 5) and the state it then read was inconclusive: nothing
/// remains to terminate, and the outcome stays unresolved.
/// </summary>
public abstract record DmsCommandOutcome
{
    /// <summary>Whether the outcome proves what happened; only <see cref="Unknown"/> does not.</summary>
    public virtual bool IsDefinite => true;

    public sealed record Succeeded() : DmsCommandOutcome;

    public sealed record Failed(DmsCommandFailure Failure) : DmsCommandOutcome;

    public sealed record Unknown(DmsServerSessionId? Session) : DmsCommandOutcome
    {
        public override bool IsDefinite => false;
    }
}

/// <summary>A <see cref="DmsCommandOutcome"/> whose success carries a value, such as the created instance's identity.</summary>
public abstract record DmsCommandOutcome<T>
{
    /// <summary>Whether the outcome proves what happened; only <see cref="Unknown"/> does not.</summary>
    public virtual bool IsDefinite => true;

    /// <summary>The same outcome without its value.</summary>
    public abstract DmsCommandOutcome WithoutValue();

    public sealed record Succeeded(T Value) : DmsCommandOutcome<T>
    {
        public override DmsCommandOutcome WithoutValue() => new DmsCommandOutcome.Succeeded();
    }

    public sealed record Failed(DmsCommandFailure Failure) : DmsCommandOutcome<T>
    {
        public override DmsCommandOutcome WithoutValue() => new DmsCommandOutcome.Failed(Failure);
    }

    public sealed record Unknown(DmsServerSessionId? Session) : DmsCommandOutcome<T>
    {
        public override bool IsDefinite => false;

        public override DmsCommandOutcome WithoutValue() => new DmsCommandOutcome.Unknown(Session);
    }
}
