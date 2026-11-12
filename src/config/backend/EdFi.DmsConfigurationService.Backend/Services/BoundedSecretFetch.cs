// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Secrets;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// The bounded lifecycle of one call to an <see cref="ISecretResolver"/>, shared by every reader that fetches through
/// <see cref="SecretValueCache"/>. Each caller keeps its own policy: how long the allowance is, how its waiters are
/// bounded, how a failure is reported, and which exception marks this fetch's own deadline.
/// </summary>
internal static class BoundedSecretFetch
{
    /// <summary>
    /// Calls the resolver on a task the host owns and races that task against <paramref name="allowance"/> on
    /// <paramref name="timeProvider"/>, so a resolver that blocks before returning its <see cref="ValueTask{TResult}"/>
    /// is bounded as well as one that returns an incomplete one. The deadline is armed before the resolver is invoked,
    /// so the window covers the whole call, and a call still queued when it passes is never started. Which task
    /// finished first decides the outcome, so a value the resolver returns after the deadline is never returned.
    /// At the deadline the resolver's token is cancelled, off this path, so a cooperative resolver stops work nobody is
    /// waiting for and its callbacks cannot delay the timeout; a thread an uncooperative one blocks is released only
    /// when it returns. The fetch then throws a new <typeparamref name="TTimedOut"/>, kept apart from any
    /// <see cref="TimeoutException"/> the resolver raises itself. The late outcome is observed and dropped, the
    /// cancellation source is disposed once both the call and its callbacks are done with it, and then
    /// <paramref name="abandonedCallObserved"/> runs. Until a call that never completes does, that continuation, the
    /// source and the call's state stay reachable from whatever the resolver keeps pending.
    /// </summary>
    public static async Task<string> FetchAsync<TTimedOut>(
        ISecretResolver resolver,
        SecretReference reference,
        TimeSpan allowance,
        TimeProvider timeProvider,
        Action abandonedCallObserved
    )
        where TTimedOut : Exception, new()
    {
        using CancellationTokenSource deadlineCancellation = new();
        Task deadline = Task.Delay(allowance, timeProvider, deadlineCancellation.Token);

        CancellationTokenSource cancellation = new();
        Task<string> call = Task.Run(
            () => resolver.ResolveAsync(reference, cancellation.Token).AsTask(),
            cancellation.Token
        );

        if (await Task.WhenAny(call, deadline) == call)
        {
            await deadlineCancellation.CancelAsync();
            cancellation.Dispose();
            return await call;
        }

        Task cancelled = cancellation.CancelAsync();
        _ = Task.WhenAll(call, cancelled)
            .ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    cancellation.Dispose();
                    abandonedCallObserved();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );

        throw new TTimedOut();
    }
}
