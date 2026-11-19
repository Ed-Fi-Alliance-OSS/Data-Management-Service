// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using Serilog.Core;
using Serilog.Events;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Replaces the <c>QueryString</c> property of a log event about an education-organization projection
/// request with a fixed placeholder, so a projection cursor never reaches a sink. Every other property
/// of the event, and every event about another route, is left unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The framework's own <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> request-starting (EventId 1)
/// and request-finished (EventId 2) events carry the raw query string as <c>QueryString</c>, beside
/// <c>Path</c>, and render it into their message. Both are logged by the hosting layer, outside the
/// endpoint, so they are written for every request, including one the endpoint rejects or never
/// reaches. A Serilog message is rendered from the event's properties when a sink writes it, so
/// replacing the property here also removes the value from the rendered message. The events
/// themselves are kept: method, path, status and elapsed time stay in the logs.
/// </para>
/// <para>
/// The route is recognized from <c>Path</c>, or from the request scope's <c>RequestPath</c>, ending in
/// <c>/management/education-organizations</c> after any number of tenant and qualifier segments,
/// matched case-insensitively and tolerating one trailing slash, as ASP.NET Core routing matches it.
/// <c>Path</c> excludes the path base. The match does not depend on whether the endpoint is mapped, so
/// a request sent while the endpoint is disabled is covered too.
/// </para>
/// </remarks>
internal sealed class EducationOrganizationProjectionQueryStringRedactingEnricher : ILogEventEnricher
{
    internal const string QueryStringPropertyName = "QueryString";
    internal const string RedactedQueryString = "?[redacted]";

    private static readonly Regex _projectionPathPattern = new(
        $"^(?:/[^/]+)*{Regex.Escape(EducationOrganizationProjectionEndpointModule.RouteSuffix)}/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (
            !logEvent.Properties.TryGetValue(QueryStringPropertyName, out LogEventPropertyValue? value)
            || value is not ScalarValue { Value: string queryString }
            || queryString.Length == 0
            || !(IsProjectionPath(logEvent, "Path") || IsProjectionPath(logEvent, "RequestPath"))
        )
        {
            return;
        }

        logEvent.AddOrUpdateProperty(
            new LogEventProperty(QueryStringPropertyName, new ScalarValue(RedactedQueryString))
        );
    }

    private static bool IsProjectionPath(LogEvent logEvent, string propertyName) =>
        logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? value)
        && value is ScalarValue { Value: string path }
        && _projectionPathPattern.IsMatch(path);
}
