// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The class of an error that proves the intended effect did not commit (a definite failure).
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

/// <summary>
/// A definite failure: <see cref="Class"/> says what the error proves about the effect, and <see cref="Retryable"/>
/// separately whether the same command can succeed later without anyone changing anything. Neither says whether
/// cleanup or recovery is safe; that stays with the caller's settlement rules.
/// </summary>
public sealed record DmsCommandFailure(DmsCommandFailureClass Class, DmsProvisioningDiagnostic Diagnostic)
{
    /// <summary>
    /// True when the server's rejection names a condition that clears on its own (a deadlock, an unavailable lock,
    /// temporary resource exhaustion), as the provider's error classifier decides; false, the default, for every other
    /// failure, including every refusal for lack of a privilege.
    /// </summary>
    public bool Retryable { get; init; }
}

/// <summary>
/// The outcome of a mutating engine command. <c>Succeeded</c> and <c>Failed</c> are definite;
/// <c>Unknown</c> means the client cannot tell whether the effect committed (timeout, cancellation after
/// send, connection loss, process killed), and names the session that must be proven terminated before
/// state is read (D-7b, D-22). An unknown outcome proves nothing beyond what it states: its state may be
/// read only after its session is proven gone, or when the engine marks it <see cref="Unknown.Quiescent"/>
/// because it already proved that no process or session of the command remains; one with
/// neither a session nor that mark cannot be resolved and permits no destructive recovery.
/// <see cref="Residue"/> lists what the command left that the caller must account for, such as a
/// replay process whose end could not be confirmed.
/// </summary>
public abstract record DmsCommandOutcome
{
    /// <summary>Whether the outcome proves what happened; only <see cref="Unknown"/> does not.</summary>
    public virtual bool IsDefinite => true;

    /// <summary>Resources the command left behind that the caller must report; empty for most outcomes.</summary>
    public IReadOnlyList<DmsCleanupResidue> Residue { get; init; } = Array.Empty<DmsCleanupResidue>();

    public sealed record Succeeded() : DmsCommandOutcome;

    public sealed record Failed(DmsCommandFailure Failure) : DmsCommandOutcome;

    public sealed record Unknown(DmsServerSessionId? Session) : DmsCommandOutcome
    {
        public override bool IsDefinite => false;

        /// <summary>
        /// True only when the engine already proved that no process or server session of the command
        /// remains, so the state it left is stable and may be read; false, the default, proves nothing.
        /// </summary>
        public bool Quiescent { get; init; }
    }
}

/// <summary>A <see cref="DmsCommandOutcome"/> whose success carries a value, such as the created instance's identity.</summary>
public abstract record DmsCommandOutcome<T>
{
    /// <summary>Whether the outcome proves what happened; only <see cref="Unknown"/> does not.</summary>
    public virtual bool IsDefinite => true;

    /// <summary>Resources the command left behind that the caller must report; empty for most outcomes.</summary>
    public IReadOnlyList<DmsCleanupResidue> Residue { get; init; } = Array.Empty<DmsCleanupResidue>();

    /// <summary>The same outcome without its value.</summary>
    public abstract DmsCommandOutcome WithoutValue();

    public sealed record Succeeded(T Value) : DmsCommandOutcome<T>
    {
        public override DmsCommandOutcome WithoutValue() =>
            new DmsCommandOutcome.Succeeded() { Residue = Residue };
    }

    public sealed record Failed(DmsCommandFailure Failure) : DmsCommandOutcome<T>
    {
        public override DmsCommandOutcome WithoutValue() =>
            new DmsCommandOutcome.Failed(Failure) { Residue = Residue };
    }

    public sealed record Unknown(DmsServerSessionId? Session) : DmsCommandOutcome<T>
    {
        public override bool IsDefinite => false;

        /// <inheritdoc cref="DmsCommandOutcome.Unknown.Quiescent"/>
        public bool Quiescent { get; init; }

        public override DmsCommandOutcome WithoutValue() =>
            new DmsCommandOutcome.Unknown(Session) { Quiescent = Quiescent, Residue = Residue };
    }
}
