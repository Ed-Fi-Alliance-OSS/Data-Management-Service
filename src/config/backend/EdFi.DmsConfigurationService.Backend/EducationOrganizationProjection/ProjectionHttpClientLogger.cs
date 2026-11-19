// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The only HTTP logger on <see cref="DmsEducationOrganizationProjectionHttpClient.Name"/> (DMS-1440 spec §5.6). It
/// replaces the default <c>IHttpClientFactory</c> loggers, which write the full request URI (with the cursor in its
/// query) and, at Trace, every header including <c>Authorization</c>. It records the method, the sanitized path without
/// its query, the status and the elapsed time; a failure adds only the exception's type chain, never its message or the
/// exception object.
/// </summary>
public sealed class ProjectionHttpClientLogger(ILogger<ProjectionHttpClientLogger> logger) : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request) => null;

    public void LogRequestStop(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TimeSpan elapsed
    ) =>
        logger.LogInformation(
            "DMS projection HTTP {Method} {Path} returned {StatusCode} in {ElapsedMilliseconds} ms",
            request.Method.Method,
            SafePath(request),
            (int)response.StatusCode,
            (long)elapsed.TotalMilliseconds
        );

    public void LogRequestFailed(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception exception,
        TimeSpan elapsed
    ) =>
        logger.LogWarning(
            "DMS projection HTTP {Method} {Path} failed with {ExceptionTypes} in {ElapsedMilliseconds} ms",
            request.Method.Method,
            SafePath(request),
            JobDiagnostics.TypeChain(exception),
            (long)elapsed.TotalMilliseconds
        );

    /// <summary>The request path without query or fragment, made safe for a log field.</summary>
    internal static string SafePath(HttpRequestMessage request) =>
        request.RequestUri is { IsAbsoluteUri: true } uri
            ? LoggingUtility.SanitizeForLog(uri.AbsolutePath)
            : string.Empty;
}
