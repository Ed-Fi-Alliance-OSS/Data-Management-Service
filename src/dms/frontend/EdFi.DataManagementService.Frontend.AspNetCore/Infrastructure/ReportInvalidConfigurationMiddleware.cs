// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Short-circuits every request with a generic <c>500</c> when startup configuration validation
/// failed. Registered by <c>Program.cs</c> in place of routing and endpoint configuration, so it is
/// the only thing a host in this mode can answer with - <c>/health</c> included.
/// </summary>
public class ReportInvalidConfigurationMiddleware
{
    /// <summary>
    /// A constant message template with the failure text passed as the
    /// <c>{ConfigurationError}</c> parameter, never as the template itself - a brace in a
    /// validation message would otherwise be parsed as a property hole and corrupt the event.
    /// </summary>
    private const string ConfigurationErrorTemplate = "Invalid DMS configuration: {ConfigurationError}";

    /// <summary>
    /// Logs the validation failures at <c>Critical</c> once, here, because <c>UseMiddleware</c>
    /// constructs the middleware a single time while the pipeline is built rather than per
    /// request - and the failures are a startup-time fact that cannot change while the process
    /// runs.
    /// </summary>
    /// <param name="next">
    /// Required by the <c>UseMiddleware</c> convention and deliberately not stored: this middleware
    /// short-circuits every request, so there is no path on which the rest of the pipeline runs and
    /// nothing to be gained from holding a delegate that can never be invoked.
    /// </param>
    public ReportInvalidConfigurationMiddleware(
        RequestDelegate next,
        List<string> errors,
        ILogger<ReportInvalidConfigurationMiddleware> logger
    )
    {
        foreach (string error in errors)
        {
            logger.LogCritical(ConfigurationErrorTemplate, error);
        }
    }

    // Invoke touches no instance state now that the failures are logged in the constructor, but
    // UseMiddleware binds a delegate to the instance and will not accept a static method, so the
    // convention - not this method's body - is what keeps it non-static.
#pragma warning disable S2325 // Methods and properties that don't access instance data should be static
    public Task Invoke(HttpContext context)
#pragma warning restore S2325
    {
        // Deliberate short-circuit: the rest of the pipeline is never invoked. The validation
        // messages were logged at Critical once, at startup, and must never reach the response
        // body, so what a client gets is the generic Ed-Fi 500 - the same shape the Configuration
        // Service's equivalent middleware writes for the same condition.
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        return context.Response.WriteAsync(
            JsonSerializer.Serialize(
                FailureResponse.ForSystemError(CorrelationIdFor(context)),
                AspNetCoreFrontend.SharedSerializerOptions
            )
        );
    }

    /// <summary>
    /// The correlation ID this response carries, reached through the single ingestion point so a
    /// client can search the logs by what it read from the body.
    /// </summary>
    /// <remarks>
    /// In practice every call here is a cache hit, including the one where
    /// <c>CorrelationIdMaxLength</c> is itself the rejected setting, because
    /// <c>LoggingMiddleware</c> runs ahead of this middleware and caches its ingestion either
    /// way. Reached through <c>ExtractTraceIdFrom</c> rather than read from <c>Items</c> directly
    /// so that this stays one more ordinary caller of that point.
    ///
    /// No <c>catch</c> for the unreadable-configuration case, and none should be added: ingestion
    /// is total and owns that fallback, so even a reordered pipeline is answered from the same
    /// documented default by the same code rather than by a second copy of the policy here.
    /// </remarks>
    private static TraceId CorrelationIdFor(HttpContext context) =>
        AspNetCoreFrontend.ExtractTraceIdFrom(
            context.Request,
            context.RequestServices.GetRequiredService<IOptions<AppSettings>>()
        );
}
