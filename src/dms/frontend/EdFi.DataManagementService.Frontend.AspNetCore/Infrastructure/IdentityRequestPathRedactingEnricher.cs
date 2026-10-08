// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog.Core;
using Serilog.Events;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Redacts the identifier segment of the <c>RequestPath</c> property for an identity get-by-id or
/// results-poll route, using the same route match and placeholder form as
/// <see cref="IdentityRoutePathRedactor"/>. Every other route, and every event without a string
/// <c>RequestPath</c>, is left unchanged.
/// </summary>
/// <remarks>
/// ASP.NET Core hosting opens a request log scope carrying the raw request path as
/// <c>RequestPath</c>, and that scope is attached to every event logged while the request runs
/// (routing, the DMS frontend and Core layers, handler logs), not only to the hosting-diagnostics
/// events <see cref="IdentityHostingDiagnosticsFilter"/> drops. The other hosting scope values
/// (<c>RequestId</c>, <c>ConnectionId</c>) and the activity trace and span ids never carry the path.
/// </remarks>
internal sealed class IdentityRequestPathRedactingEnricher : ILogEventEnricher
{
    private const string RequestPathPropertyName = "RequestPath";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (
            !logEvent.Properties.TryGetValue(RequestPathPropertyName, out LogEventPropertyValue? value)
            || value is not ScalarValue { Value: string requestPath }
        )
        {
            return;
        }

        string redacted = IdentityRoutePathRedactor.RedactWithAnyPrefix(requestPath);
        if (!ReferenceEquals(redacted, requestPath))
        {
            logEvent.AddOrUpdateProperty(
                new LogEventProperty(RequestPathPropertyName, new ScalarValue(redacted))
            );
        }
    }
}
