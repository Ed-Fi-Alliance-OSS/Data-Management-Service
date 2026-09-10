// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// The connection-acquisition boundary every read-path seam runs its acquisition inside. One
/// definition, so all seven seams classify the same failures the same way and a new seam has an
/// obvious thing to call.
/// </summary>
internal static class ConnectionAcquisition
{
    /// <summary>
    /// Runs one seam's acquisition - data-source and connection construction, connection-string
    /// parsing, and the open call - and reports an unreachable snapshot as
    /// <see cref="DatabaseConnectionUnavailableException" />.
    /// </summary>
    /// <param name="acquireAsync">
    /// The whole acquisition, not just the open. Parsing precedes the open at every seam, so a
    /// boundary drawn around the open alone would let a provider-invalid connection string escape as
    /// an unhandled provider argument failure.
    /// </param>
    /// <param name="targetKind">The kind of target this request selected.</param>
    /// <param name="isExpectedFailure">
    /// The provider's classification of an expected connection-establishment failure. Consulted only
    /// for a snapshot, because <c>&amp;&amp;</c> short-circuits, so it cannot affect any other kind.
    /// </param>
    /// <param name="describeFailure">
    /// The engine's short, log-safe description of what the provider raised - its type plus the
    /// provider's own error code. Supplied by the engine because the codes worth having are
    /// provider-specific and this assembly references neither driver. It is the only thing about the
    /// provider exception that reaches a log or travels on the wrapper.
    /// </param>
    /// <param name="cancellationToken">
    /// The caller's token, used only to decide whether a cancellation is the caller's own. Seam 1 has
    /// no token and passes <see cref="CancellationToken.None" />: an
    /// <see cref="OperationCanceledException" /> there is not attributable to a caller, so it is
    /// classified like any other failure rather than rethrown as a cancellation.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The wrapper is constructed only for a snapshot, and that condition is load-bearing.</b>
    /// <see cref="DatabaseConnectionUnavailableException" /> is deliberately not a <c>DbException</c>,
    /// so wrapping every kind would change two contracts that must not move:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// SQL Server write-session creation opens through the same helper the read seams use, and
    /// <c>DefaultRelationalWriteExecutor</c> catches <c>DbException</c> from it to route session-creation
    /// failures through the write-failure mapper. A wrapped connection timeout would escape that mapper.
    /// </item>
    /// <item>
    /// On a read with custom views configured, the custom-view <c>catch (DbException)</c> sites turn a
    /// connection failure into a <c>problem+json</c> 500. A wrapped primary or read-replica failure
    /// would become the plain 500 from the unhandled path instead.
    /// </item>
    /// </list>
    /// <para>
    /// Restricting construction to a snapshot keeps both byte-identical while still classifying at
    /// every seam; the classification simply has no consumer for the two kinds whose behavior must not
    /// change. It also makes the guard a no-op on the write path by construction, because a mutation
    /// can never select a snapshot - target selection rejects it before any target is assigned - so the
    /// shared SQL Server opener can carry the guard without a second read-only entry point.
    /// </para>
    /// </remarks>
    public static async Task<T> GuardAsync<T>(
        Func<Task<T>> acquireAsync,
        EffectiveTargetKind targetKind,
        Predicate<Exception> isExpectedFailure,
        Func<Exception, string> describeFailure,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(acquireAsync);
        ArgumentNullException.ThrowIfNull(isExpectedFailure);
        ArgumentNullException.ThrowIfNull(describeFailure);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return await acquireAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // An aborted or timed-out caller is not evidence of a missing snapshot, and translating it
            // would diverge from primary and read-replica cancellation behavior.
            throw;
        }
        catch (ObjectDisposedException)
        {
            // Shutdown or disposal of a data source is not an unavailable database.
            throw;
        }
        catch (Exception exception)
            when (targetKind == EffectiveTargetKind.Snapshot && isExpectedFailure(exception))
        {
            // The engine's description - the exception's type plus the provider's own error code -
            // never the exception itself and never its message, data, or inner exceptions.
            // Acquisition parses the connection string, and a provider failure there quotes the
            // offending value back. The code does not: it is what lets an operator tell a wrong
            // password from an absent catalog from an unreachable host, which the type alone cannot.
            // S6667 asks for the caught exception to be passed to the logger. That is the right
            // default and the wrong thing here, for the reason above: the exception carries the
            // untrusted value. The description is logged instead, which is the part that helps an
            // operator without carrying anything the provider put in it.
            string failureDescription = describeFailure(exception);

#pragma warning disable S6667
            logger.LogWarning(
                "Connection acquisition failed with {Failure} for the {TargetKind} target",
                failureDescription,
                targetKind
            );
#pragma warning restore S6667

            // The provider exception travels as the inner exception for diagnostics only. It is never
            // logged and never assigned to the request's caught-exception field; the description
            // travels alongside it so the translation sites in Core can log what was logged here.
            throw new DatabaseConnectionUnavailableException(targetKind, failureDescription, exception);
        }
    }
}
