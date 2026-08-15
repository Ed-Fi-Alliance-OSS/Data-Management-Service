// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Converts exceptions escaping the core pipeline into error responses: 403 for
/// authorization failures, 503 when the backend circuit is open, 500 otherwise. The 500-path
/// exception is captured on the request so the outer request logging middleware attaches it to the
/// structured request-failure event; this middleware does not log it.
/// </summary>
/// <param name="_circuitBreakDuration">
/// Break duration quoted as <c>Retry-After</c> on a circuit-open 503. Required rather than
/// defaulted: a construction site that forgot it would silently serve the 503 with no retry hint,
/// which is the difference between telling a client when to come back and telling it nothing. Pass
/// null only where the circuit-open path is genuinely out of scope for the caller.
/// </param>
internal class CoreExceptionLoggingMiddleware(ILogger _logger, TimeSpan? _circuitBreakDuration)
    : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering CoreExceptionLoggingMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            await next();
        }
        catch (AuthorizationException ex)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: (int)HttpStatusCode.Forbidden,
                Body: FailureResponse.ForForbidden(
                    traceId: requestInfo.FrontendRequest.TraceId,
                    errors: [ex.Message]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
        }
        catch (CustomViewAuthorizationValidationException ex)
        {
            requestInfo.CaughtException = ex;
            requestInfo.FrontendResponse = CreateSystemErrorResponse(requestInfo.FrontendRequest.TraceId);
        }
        catch (BrokenCircuitException)
        {
            // The circuit is open because the backend is shedding load, so the request never reached
            // the database and is safe to replay. Serving it as a retriable 503 with the break
            // duration as Retry-After is what keeps a client from treating a transient outage as a
            // permanent rejection and dropping the document.
            //
            // Deliberately not recorded as a caught exception. This is the designed outcome of a
            // mechanism that already announced itself: the breaker logs the transition once when it
            // opens, and each refusal is visible as a 503 in the request log. Attaching the
            // exception would instead emit one error-level entry carrying a full stack trace for
            // every request refused during the break - under bulk load, thousands of copies of a
            // fact already reported.
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForServiceUnavailable(requestInfo.FrontendRequest.TraceId),
                Headers: RetryAfterHeaderFor(_circuitBreakDuration),
                ContentType: "application/problem+json"
            );
        }
        catch (OperationCanceledException) when (requestInfo.RequestCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            requestInfo.CaughtException = ex;
            // Replace the frontend response (if any) with a 500 error
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForServerErrorMessageBody(
                    "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
        }
    }

    /// <summary>
    /// Formats the configured break duration as whole seconds per RFC 9110 delta-seconds, rounding
    /// up so the hint never expires before the circuit does. It is the full break rather than the
    /// remaining break, which errs late by design: a client that waits too long merely retries
    /// later, while one that returns early adds load to a backend that is still shedding it.
    /// Omitted when no duration is configured, since a header is better absent than guessed.
    /// </summary>
    private static Dictionary<string, string> RetryAfterHeaderFor(TimeSpan? retryAfter) =>
        retryAfter is { } delay && delay > TimeSpan.Zero
            ? new Dictionary<string, string>
            {
                ["Retry-After"] = ((int)Math.Ceiling(delay.TotalSeconds)).ToString(
                    CultureInfo.InvariantCulture
                ),
            }
            : [];

    // ForSystemError emits a ProblemDetails body, so it must be served as problem+json rather than
    // inheriting FrontendResponse's application/json default. The generic 500 above is deliberately
    // left alone: its body is not ProblemDetails.
    private static FrontendResponse CreateSystemErrorResponse(TraceId traceId) =>
        new(
            StatusCode: 500,
            Body: FailureResponse.ForSystemError(traceId),
            Headers: [],
            ContentType: "application/problem+json"
        );
}
