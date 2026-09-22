// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Identity;

/// <summary>
/// The three-call-site sanitized boundary around a request-scoped <see cref="IIdentityService" />
/// (design.md "Provider Execution and Exception Boundary", D9): request-scoped activation
/// (<see cref="Activate" />), the <c>Capabilities</c> getter (<see cref="ReadCapabilities" />), and an
/// identity operation invocation (<see cref="InvokeAsync{T}" />). Each call site checks the request's
/// cancellation token before it runs, so a cancelled request never enters the guarded call, and each
/// narrowly catches only that one call so unrelated host validation, authorization, or
/// response-mapping failures are never relabeled as provider failures.
/// <para>
/// An exception from activation or the capability getter sets a sanitized
/// <c>identities:provider-configuration</c> 500 on <see cref="RequestInfo.FrontendResponse" /> and
/// returns null/default so the caller invokes no operation. An exception from an operation invocation
/// sets a sanitized <c>identities:upstream-failure</c> 502 instead. An
/// <see cref="OperationCanceledException" /> observed while the request's own token is cancelled is
/// never treated as a provider failure: it is rethrown as a fresh
/// <see cref="OperationCanceledException" /> carrying only the request's token, so a provider's own
/// exception message and inner exception - which may quote submitted person data - never reach outer
/// cancellation logging. The original exception is logged at <c>Debug</c> only in that case.
/// </para>
/// <para>
/// Host failure-level logs for all three call sites carry only the exception type name, the stage,
/// the operation, and the trace id, plus <see cref="Exception.StackTrace" /> - never
/// <see cref="Exception.Message" /> and never <see cref="object.ToString" /> on the exception, both of
/// which can carry provider-supplied text. The full exception (message, inner exceptions, and stack
/// trace) is available only at <c>Debug</c>. <see cref="RequestInfo.CaughtException" /> is never
/// assigned by this boundary: that field is read by
/// <see cref="Middleware.RequestResponseLoggingMiddleware" /> and would otherwise carry the raw,
/// unsanitized provider exception into the request-completion log.
/// </para>
/// </summary>
internal sealed class IdentityProviderBoundary(ILogger<IdentityProviderBoundary> _logger)
{
    /// <summary>
    /// The stage a caught exception occurred in, for the sanitized failure-level log line.
    /// </summary>
    private enum Stage
    {
        Activation,
        Capabilities,
        Invoke,
    }

    /// <summary>
    /// Resolves the registered <see cref="IIdentityService" /> from the request's own scope. A
    /// throwing registration factory, constructor, or scoped dependency is caught here; the caller
    /// invokes no operation when this returns null.
    /// </summary>
    public IIdentityService? Activate(RequestInfo requestInfo)
    {
        requestInfo.RequestCancellationToken.ThrowIfCancellationRequested();

        try
        {
            return requestInfo.ScopedServiceProvider.GetRequiredService<IIdentityService>();
        }
        catch (OperationCanceledException ex)
            when (requestInfo.RequestCancellationToken.IsCancellationRequested)
        {
            throw SanitizeCancellation(ex, requestInfo, Stage.Activation);
        }
        catch (Exception ex)
        {
            FailConfiguration(ex, requestInfo, Stage.Activation);
            return null;
        }
    }

    /// <summary>
    /// Reads <see cref="IIdentityService.Capabilities" /> exactly once for the request. A throwing
    /// getter is caught here; the caller invokes no operation when this returns null.
    /// </summary>
    public IdentityCapabilities? ReadCapabilities(IIdentityService provider, RequestInfo requestInfo)
    {
        requestInfo.RequestCancellationToken.ThrowIfCancellationRequested();

        try
        {
            return provider.Capabilities;
        }
        catch (OperationCanceledException ex)
            when (requestInfo.RequestCancellationToken.IsCancellationRequested)
        {
            throw SanitizeCancellation(ex, requestInfo, Stage.Capabilities);
        }
        catch (Exception ex)
        {
            FailConfiguration(ex, requestInfo, Stage.Capabilities);
            return null;
        }
    }

    /// <summary>
    /// Invokes one identity operation call inside the sanitized boundary. A throwing operation is
    /// caught here and produces a sanitized upstream-failure 502; it establishes no terminal job
    /// state. The caller maps no result when this returns null.
    /// </summary>
    public async Task<T?> InvokeAsync<T>(Func<Task<T>> operation, RequestInfo requestInfo)
        where T : class
    {
        requestInfo.RequestCancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await operation();
        }
        catch (OperationCanceledException ex)
            when (requestInfo.RequestCancellationToken.IsCancellationRequested)
        {
            throw SanitizeCancellation(ex, requestInfo, Stage.Invoke);
        }
        catch (Exception ex)
        {
            FailUpstream(ex, requestInfo);
            return null;
        }
    }

    /// <summary>
    /// Logs the original exception at Debug only - never at a higher level - and returns a fresh
    /// <see cref="OperationCanceledException" /> carrying just the request's cancellation token, with
    /// no provider message or inner exception.
    /// </summary>
    private OperationCanceledException SanitizeCancellation(
        OperationCanceledException ex,
        RequestInfo requestInfo,
        Stage stage
    )
    {
        _logger.LogDebug(
            ex,
            "Identity provider observed request cancellation during {Stage} for {Operation} - TraceId: {TraceId}",
            stage,
            requestInfo.IdentityOperation,
            requestInfo.FrontendRequest.TraceId.Value
        );

        return new OperationCanceledException(requestInfo.RequestCancellationToken);
    }

    private void FailConfiguration(Exception ex, RequestInfo requestInfo, Stage stage)
    {
        LogSanitizedFailure(ex, requestInfo, stage);

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 500,
            Body: IdentityFailureResponse.ForIdentityProviderConfiguration(
                requestInfo.FrontendRequest.TraceId
            ),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }

    private void FailUpstream(Exception ex, RequestInfo requestInfo)
    {
        LogSanitizedFailure(ex, requestInfo, Stage.Invoke);

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 502,
            Body: IdentityFailureResponse.ForIdentityUpstreamFailure(requestInfo.FrontendRequest.TraceId),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }

    /// <summary>
    /// The one place both failure branches log from: exception type name, stage, operation, and trace
    /// id at Error, with <see cref="Exception.StackTrace" /> - never <see cref="Exception.Message" />
    /// and never <c>ex.ToString()</c> - and the full exception at Debug only.
    /// </summary>
    private void LogSanitizedFailure(Exception ex, RequestInfo requestInfo, Stage stage)
    {
#pragma warning disable S6667
        _logger.LogError(
            "Identity provider threw {ExceptionType} during {Stage} for {Operation} - TraceId: {TraceId}. {StackTrace}",
            ex.GetType().Name,
            stage,
            requestInfo.IdentityOperation,
            requestInfo.FrontendRequest.TraceId.Value,
            ex.StackTrace
        );
#pragma warning restore S6667

        _logger.LogDebug(
            ex,
            "Identity provider failure detail during {Stage} for {Operation} - TraceId: {TraceId}",
            stage,
            requestInfo.IdentityOperation,
            requestInfo.FrontendRequest.TraceId.Value
        );
    }
}
