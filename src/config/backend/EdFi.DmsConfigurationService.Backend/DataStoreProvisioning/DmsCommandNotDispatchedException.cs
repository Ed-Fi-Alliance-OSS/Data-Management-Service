// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// A command stopped before it was handed to the driver (D-12): the holder refused it before invoking it (loss,
/// or cancellation while it waited for the execution gate), or the provider's own check before sending stopped
/// it. Only this exception proves that nothing of the command was sent, so the caller keeps the last known state
/// of the target. Any other cancellation of a command proves nothing: once a mutation may have reached the
/// driver, it stays in doubt whatever ends it. It is an <see cref="OperationCanceledException"/> carrying the
/// token that stopped it, so caller cancellation, deadlines and loss are told apart as before. The original
/// exception is not kept, because a driver's message may quote the connection.
/// </summary>
public sealed class DmsCommandNotDispatchedException : OperationCanceledException
{
    private const string NotDispatched = "The command was stopped before it was dispatched.";

    public DmsCommandNotDispatchedException(CancellationToken cancellationToken)
        : base(NotDispatched, cancellationToken) { }

    public DmsCommandNotDispatchedException(OperationCanceledException canceled)
        : base(NotDispatched, TokenOf(canceled)) { }

    /// <summary>A provider's explicit check before it hands a mutation to the driver.</summary>
    public static void ThrowIfCancellationRequested(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new DmsCommandNotDispatchedException(cancellationToken);
        }
    }

    private static CancellationToken TokenOf(OperationCanceledException canceled)
    {
        ArgumentNullException.ThrowIfNull(canceled);
        return canceled.CancellationToken;
    }
}
