// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// Withholds the content of every exception raised on a route marked with
/// <see cref="ExceptionTypeOnlyLoggingMetadata"/> before it reaches the framework's exception
/// middleware, which logs an exception it cannot answer (a response that had already started, or an
/// error handler that failed) with the exception attached (DMS-1327 D-15, D-17). Runs immediately after
/// routing, so it wraps everything the route runs, endpoint parameter binding included: a
/// <c>[FromServices]</c> resolution can run registration factories and constructors a plugin supplies.
/// </summary>
/// <remarks>
/// The replacement keeps the category the exception middleware and <see cref="GlobalExceptionHandler"/>
/// act on: a malformed form, an unreadable request with its status code, and a caller cancellation;
/// anything else is a fault answered 500. No replacement carries the original as its inner exception or
/// any of its <c>Data</c>; the original type names are kept on <see cref="WithheldExceptionFeature"/> for
/// the failed-request log event.
/// </remarks>
public class ExceptionContentBoundaryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ExceptionTypeOnlyLoggingMetadata>() is null)
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            string exceptionTypes = ExceptionTypeNames.Chain(exception);
            context.Features.Set(new WithheldExceptionFeature(exceptionTypes));
            throw Replacement(context, exception, exceptionTypes);
        }
    }

    private static Exception Replacement(HttpContext context, Exception exception, string exceptionTypes) =>
        exception switch
        {
            OperationCanceledException when context.RequestAborted.IsCancellationRequested =>
                new OperationCanceledException(
                    $"The request was cancelled ({exceptionTypes}).",
                    context.RequestAborted
                ),
            InvalidDataException => new InvalidDataException(
                $"The request form payload is malformed ({exceptionTypes})."
            ),
            BadHttpRequestException badHttpRequest => new BadHttpRequestException(
                $"The request could not be read ({exceptionTypes}).",
                badHttpRequest.StatusCode
            ),
            _ => new InvalidOperationException(
                $"The request failed with {exceptionTypes}; the exception's content was withheld."
            ),
        };
}
