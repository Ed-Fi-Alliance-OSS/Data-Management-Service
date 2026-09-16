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
    /// A constant message template with the failure text passed as a parameter, never the failure
    /// text as the template itself.
    /// </summary>
    /// <remarks>
    /// The logging pipeline re-reads the template from <c>{OriginalFormat}</c> and parses it for
    /// property holes, so a validation message passed as the template would have any brace it
    /// contained read as a hole rather than as literal text - corrupting the structured event and,
    /// with an unbalanced brace, the rendered message too. Passing the message as
    /// <c>{ConfigurationError}</c> makes it data, which no validator can turn back into a template.
    /// </remarks>
    private const string ConfigurationErrorTemplate = "Invalid DMS configuration: {ConfigurationError}";

    /// <summary>
    /// Logs the validation failures once, here, because <c>UseMiddleware</c> constructs the
    /// middleware a single time while the pipeline is built rather than per request. The failures
    /// are a startup-time fact about configuration that cannot change while the process runs, so
    /// re-logging them at <c>Critical</c> on every request would add nothing beyond traffic-rate
    /// noise in the sink an operator watches most closely.
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
    /// The correlation ID this response carries, reached the same way <c>LoggingMiddleware</c>
    /// reaches the one it logs for the same request, so a client can still search the logs by what
    /// it read from the body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In practice every call here is a cache hit, including the one where
    /// <c>CorrelationIdMaxLength</c> is itself the rejected setting. <c>LoggingMiddleware</c> is
    /// registered ahead of this middleware and caches its ingestion on <c>HttpContext.Items</c>
    /// either way: from validated configuration when it can read it, and from
    /// <see cref="AppSettings.DefaultCorrelationIdMaxLength"/> when reading it is what failed. The
    /// value is reached through <c>ExtractTraceIdFrom</c> rather than read from <c>Items</c>
    /// directly so that this stays one more ordinary caller of the single ingestion point.
    /// </para>
    /// <para>
    /// No <c>catch</c> for the unreadable-configuration case, and none should be added here: the
    /// ingestion point is total and owns that fallback, so a request this middleware ever answers
    /// without <c>LoggingMiddleware</c> having ingested it first - a pipeline reordering in
    /// <c>Program.cs</c>, say - is answered from the same documented default by the same code, not
    /// by a second copy of the policy living at this call site.
    /// </para>
    /// </remarks>
    private static TraceId CorrelationIdFor(HttpContext context) =>
        AspNetCoreFrontend.ExtractTraceIdFrom(
            context.Request,
            context.RequestServices.GetRequiredService<IOptions<AppSettings>>()
        );
}
