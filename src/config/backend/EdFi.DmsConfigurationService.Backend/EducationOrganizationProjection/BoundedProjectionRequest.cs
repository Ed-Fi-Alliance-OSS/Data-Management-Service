// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>The outcome of a request, or the interruption that replaced it (<c>Timeout</c> or <c>NetworkError</c>).</summary>
internal readonly record struct BoundedRequestResult<T>(T Outcome, Code? Interruption);

/// <summary>
/// Runs one DMS request of the projection reader bounded by its stage timeout and the read deadline (DMS-1440 spec
/// §5.4, §5.5). However the request ends, normally or with an exception, caller cancellation is checked first and the
/// timeout second, so an outcome that arrives after either is never classified, cached or returned: cancellation
/// throws with the caller's token, and an expired timeout is <c>Timeout</c> even when a transport exception followed
/// it. Any other cancellation or transport exception is <c>NetworkError</c>; other exceptions propagate as defects.
/// </summary>
internal static class BoundedProjectionRequest
{
    /// <param name="request">Sends the request and reads its response, observing the token it is given.</param>
    /// <param name="timeout">The stage timeout.</param>
    /// <param name="start">When the request starts; the caller has checked that it is before <paramref name="readDeadline"/>.</param>
    /// <param name="readDeadline">When the whole logical read times out.</param>
    /// <param name="timeProvider">The clock and timers.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public static async Task<BoundedRequestResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> request,
        TimeSpan timeout,
        DateTimeOffset start,
        DateTimeOffset readDeadline,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset requestDeadline = start + timeout < readDeadline ? start + timeout : readDeadline;

        using CancellationTokenSource timeoutSource = new(requestDeadline - start, timeProvider);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );

        // The timer covers a timeout that fires before the clock reads the due time; the clock covers one whose timer
        // has not run yet when the request ends.
        bool TimedOut() =>
            timeoutSource.IsCancellationRequested || timeProvider.GetUtcNow() >= requestDeadline;

        T outcome;
        try
        {
            outcome = await request(linkedSource.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
            when (TimedOut()
                && (
                    exception is OperationCanceledException
                    || ProjectionFailureClassifier.IsTransportFailure(exception)
                )
            )
        {
            return new BoundedRequestResult<T>(default!, Code.Timeout);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException
                || ProjectionFailureClassifier.IsTransportFailure(exception)
            )
        {
            return new BoundedRequestResult<T>(default!, Code.NetworkError);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return TimedOut()
            ? new BoundedRequestResult<T>(default!, Code.Timeout)
            : new BoundedRequestResult<T>(outcome, null);
    }
}
