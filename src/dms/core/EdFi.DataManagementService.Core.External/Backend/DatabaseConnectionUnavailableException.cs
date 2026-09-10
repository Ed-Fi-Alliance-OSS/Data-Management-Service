// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Thrown when a read-path connection to the request's selected target could not be acquired:
/// data-source or connection construction, connection-string parsing, or the open call itself. It
/// describes a database that cannot be reached, as distinct from a failure raised after a connection
/// is open, which keeps its own contract.
/// </summary>
/// <remarks>
/// <para>
/// Backend-neutral by design: it names only the target kind, so Core can decide the response without
/// knowing which provider raised the underlying failure. A selected <c>Snapshot</c> becomes Snapshot
/// Not Found; a <c>Primary</c> or <c>ReadReplica</c> keeps the response it produces today.
/// </para>
/// <para>
/// <b>The base type is deliberately <see cref="Exception" /> and nothing more specific.</b> The read
/// path is full of catches that would otherwise intercept this before it reaches the middleware that
/// translates it, and each interception would silently change a contract the acceptance criteria
/// require to be retained:
/// </para>
/// <list type="bullet">
/// <item>
/// Not a <c>DbException</c>: the custom-view wrappers in <c>RelationalDocumentStoreRepository</c> and
/// <c>DescriptorReadHandler</c>, <c>CustomViewAuthorizationValidator</c>, and the write-path transient
/// classifiers all catch that type. A wrapped connection failure would be relabeled a custom-view
/// validation failure, or routed through the write-failure mapper.
/// </item>
/// <item>
/// Not an <c>InvalidOperationException</c>: <c>DescriptorReadHandler</c> catches that type around the
/// command executor's own <c>ExecuteReaderAsync</c>, and the repository catches it around query
/// preprocessing, which resolves references through the same connection seams. Either would convert
/// this into an <c>UnknownFailure</c> carrying the message.
/// </item>
/// </list>
/// <para>
/// That the type is caught nowhere else is what keeps the translation to a handful of deliberate sites
/// in Core rather than an edit at every call site on the read path.
/// </para>
/// <para>
/// The provider exception is retained as <see cref="Exception.InnerException" /> for diagnostics, but a
/// provider quotes the offending connection string in its message. It must therefore never be handed to
/// a logger, and never assigned to the request's caught-exception field, whose message chain is logged.
/// Log <see cref="FailureDescription" /> and <see cref="TargetKind" /> instead - together they are the
/// whole of what is safe to record, and enough to tell the causes apart.
/// </para>
/// </remarks>
public sealed class DatabaseConnectionUnavailableException : Exception
{
    public DatabaseConnectionUnavailableException(
        EffectiveTargetKind targetKind,
        string failureDescription,
        Exception innerException
    )
        : base(BuildMessage(targetKind), innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDescription);

        TargetKind = targetKind;
        FailureDescription = failureDescription;
    }

    /// <summary>
    /// The kind of target whose connection could not be acquired. The response depends on this, so it
    /// is carried on the exception rather than re-read from request state at the translation site.
    /// </summary>
    public EffectiveTargetKind TargetKind { get; }

    /// <summary>
    /// The engine's short, log-safe description of what the provider raised - its exception type and,
    /// where the provider exposes one, its own error code, as in <c>SqlException(4060)</c> or
    /// <c>PostgresException(3D000)</c>.
    /// </summary>
    /// <remarks>
    /// Composed by the engine rather than read off the exception here, because the codes worth having
    /// are provider-specific: SQL Server carries <c>SqlException.Number</c> and PostgreSQL carries
    /// <c>PostgresException.SqlState</c>, and the engine-agnostic <c>DbException.SqlState</c> is null
    /// for SqlClient. It is carried on the exception so the translation sites in Core - which cannot see
    /// provider types - log the same description the acquisition boundary did.
    /// <para>
    /// It is a code and a type name and nothing else. That is what makes it the one diagnostic that may
    /// accompany <see cref="TargetKind" /> into a log: a wrong password, an absent catalog, and an
    /// unreachable host are three different codes and three different remediations, and none of them
    /// quotes the connection string the way a provider message does.
    /// </para>
    /// </remarks>
    public string FailureDescription { get; }

    /// <summary>
    /// Names the target kind and nothing else. The connection string is deliberately absent: this
    /// message is the one part of the exception that is safe to log.
    /// </summary>
    private static string BuildMessage(EffectiveTargetKind targetKind) =>
        $"A database connection could not be acquired for the {targetKind} target.";
}
