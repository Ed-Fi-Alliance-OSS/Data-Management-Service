// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>The ownership state of one job execution (spec D-7a).</summary>
public enum JobOwnershipState
{
    /// <summary>The execution holds a lease that its last renewal confirmed.</summary>
    Owned,

    /// <summary>
    /// A renewal failed or a write's result is unknown. Terminal for this execution: no further ownership-
    /// dependent write is issued, and recovery follows whatever the database persisted.
    /// </summary>
    Uncertain,

    /// <summary>The execution is issuing its single outcome write.</summary>
    Finalizing,

    /// <summary>The outcome write committed.</summary>
    Finalized,
}

/// <summary>
/// The ownership state and the gate of one job execution (spec D-7a). Renewal, fences, and finalization all
/// enter the gate, so they never overlap, and a renewal waiting for the gate is admitted before any other
/// waiter, so successive fences cannot starve renewal.
/// </summary>
/// <remarks>
/// <para>
/// State transitions are atomic compare-and-set operations. <see cref="JobOwnershipState.Uncertain"/> takes
/// precedence: once entered, it is never left, it keeps its first reason, and it prevents
/// <see cref="TryBeginFinalizing"/>, so an execution that lost certainty never issues an outcome write.
/// Callers make their transitions while holding the gate, so a decision and the state it read cannot be
/// separated by a concurrent renewal.
/// </para>
/// <para>
/// The gate hands itself directly to the next waiter on release: waiting renewals first, then fences and
/// finalization in arrival order. A waiter whose cancellation token fires before it is handed the gate is
/// removed and never receives it.
/// </para>
/// </remarks>
public sealed class JobExecutionOwnership
{
    private readonly object _sync = new();
    private readonly LinkedList<GateWaiter> _renewalWaiters = new();
    private readonly LinkedList<GateWaiter> _otherWaiters = new();
    private JobOwnershipState _state = JobOwnershipState.Owned;
    private string? _reason;
    private bool _gateHeld;

    public JobOwnershipState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>The reason recorded when the state became <see cref="JobOwnershipState.Uncertain"/>.</summary>
    public string? Reason
    {
        get
        {
            lock (_sync)
            {
                return _reason;
            }
        }
    }

    /// <summary>
    /// Moves <see cref="JobOwnershipState.Owned"/> or <see cref="JobOwnershipState.Finalizing"/> to
    /// <see cref="JobOwnershipState.Uncertain"/> with <paramref name="reason"/>. Returns false, changing
    /// nothing, when the state is already uncertain or finalized.
    /// </summary>
    public bool TryMarkUncertain(string reason)
    {
        lock (_sync)
        {
            if (_state is not (JobOwnershipState.Owned or JobOwnershipState.Finalizing))
            {
                return false;
            }

            _state = JobOwnershipState.Uncertain;
            _reason = reason;
            return true;
        }
    }

    /// <summary>
    /// Moves <see cref="JobOwnershipState.Owned"/> to <see cref="JobOwnershipState.Finalizing"/>. Returns false
    /// in any other state, in particular after the execution became uncertain.
    /// </summary>
    public bool TryBeginFinalizing() => TryTransition(JobOwnershipState.Owned, JobOwnershipState.Finalizing);

    /// <summary>Moves <see cref="JobOwnershipState.Finalizing"/> to <see cref="JobOwnershipState.Finalized"/>.</summary>
    public bool TryMarkFinalized() =>
        TryTransition(JobOwnershipState.Finalizing, JobOwnershipState.Finalized);

    /// <summary>
    /// Enters the gate for a renewal, ahead of every waiting fence and finalization. Dispose the result to
    /// leave the gate.
    /// </summary>
    public Task<IDisposable> EnterForRenewalAsync(CancellationToken cancellationToken) =>
        EnterAsync(_renewalWaiters, cancellationToken);

    /// <summary>
    /// Enters the gate for a fence or for finalization, after any renewal that is waiting. Dispose the result
    /// to leave the gate.
    /// </summary>
    public Task<IDisposable> EnterAsync(CancellationToken cancellationToken) =>
        EnterAsync(_otherWaiters, cancellationToken);

    private bool TryTransition(JobOwnershipState from, JobOwnershipState to)
    {
        lock (_sync)
        {
            if (_state != from)
            {
                return false;
            }

            _state = to;
            return true;
        }
    }

    private Task<IDisposable> EnterAsync(LinkedList<GateWaiter> queue, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IDisposable>(cancellationToken);
        }

        GateWaiter waiter;
        lock (_sync)
        {
            if (!_gateHeld)
            {
                _gateHeld = true;
                return Task.FromResult<IDisposable>(new GateLease(this));
            }

            waiter = new GateWaiter();
            waiter.Node = queue.AddLast(waiter);

            // Registered under the lock so that Release, which disposes the registration after dequeuing
            // under the same lock, always sees it. A token cancelled meanwhile runs Abandon synchronously
            // here, which re-enters the lock on this thread.
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() => Abandon(waiter, cancellationToken));
            }
        }

        return waiter.Completion.Task;
    }

    /// <summary>Removes a waiter that was cancelled before it was handed the gate.</summary>
    private void Abandon(GateWaiter waiter, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (waiter.Node?.List is not { } queue)
            {
                return;
            }

            queue.Remove(waiter.Node);
        }

        waiter.Completion.TrySetCanceled(cancellationToken);
    }

    private void Release()
    {
        GateWaiter? next;
        lock (_sync)
        {
            next = Dequeue(_renewalWaiters) ?? Dequeue(_otherWaiters);
            if (next is null)
            {
                _gateHeld = false;
                return;
            }
        }

        next.Registration.Dispose();
        next.Completion.TrySetResult(new GateLease(this));
    }

    private static GateWaiter? Dequeue(LinkedList<GateWaiter> queue)
    {
        if (queue.First is not { } first)
        {
            return null;
        }

        queue.RemoveFirst();
        return first.Value;
    }

    private sealed class GateWaiter
    {
        public TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<GateWaiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class GateLease(JobExecutionOwnership owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
