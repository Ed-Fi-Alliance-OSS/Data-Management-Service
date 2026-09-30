// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog.Events;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Suppresses the framework's own <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> request-start and
/// request-finish log events (and the routing matcher's Debug-level candidate events, see remarks) for an identity get-by-id or results-poll route, because those events
/// carry the identifier verbatim in both their <c>Path</c> and <c>RequestPath</c> properties and in
/// the rendered message. The match is case-insensitive and tolerates a single trailing slash
/// on the route, mirroring how ASP.NET Core routing matches these paths before this filter ever sees
/// them. Every other route, including the other three identity routes (none of which carries an
/// identifier) and their trailing-slash forms, keeps its framework events unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Wired into <see cref="LoggingConfigurator.ConfigureLogging"/> as
/// <c>.Filter.ByExcluding(IdentityHostingDiagnosticsFilter.Matches)</c>, so <see cref="Matches"/>
/// must have exactly the <c>Func&lt;LogEvent, bool&gt;</c> shape Serilog's filter expects.
/// </para>
/// <para>
/// Grounded in the framework's own hosting-diagnostics events: the request-starting (EventId 1) and
/// request-finished (EventId 2) events, both Information, carry both <c>Path</c> and
/// <c>RequestPath</c> simultaneously holding the identical value, so this checks both rather than
/// picking one. A third, unrelated event on the same <c>SourceContext</c>
/// (<c>HostingStartupAssemblyLoaded</c>, Debug, EventId 13) carries neither property, so every
/// property read here is guarded by a presence check before it is compared - an unguarded indexer
/// read would throw or, if defaulted, misfire on that unrelated event.
/// </para>
/// <para>
/// The routing matcher's Debug-level candidate events (<c>Microsoft.AspNetCore.Routing.Matching.DfaMatcher</c>,
/// "candidate(s) found for the request path" and "is valid for the request path") carry the same raw
/// path in their <c>Path</c> property and rendered message, so they are dropped for the same routes
/// when an operator lowers the routing log level to Debug.
/// </para>
/// <para>
/// Serilog runs enrichers before filters, so by the time this runs
/// <see cref="IdentityRequestPathRedactingEnricher"/> has already redacted <c>RequestPath</c>; the
/// raw <c>Path</c> property is what still identifies these events.
/// </para>
/// </remarks>
internal static class IdentityHostingDiagnosticsFilter
{
    private const string HostingDiagnosticsSourceContext = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string RoutingMatcherSourceContext = "Microsoft.AspNetCore.Routing.Matching.DfaMatcher";

    public static bool Matches(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (
            !logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContextValue)
            || sourceContextValue is not ScalarValue { Value: string sourceContext }
            || !(
                string.Equals(sourceContext, HostingDiagnosticsSourceContext, StringComparison.Ordinal)
                || string.Equals(sourceContext, RoutingMatcherSourceContext, StringComparison.Ordinal)
            )
        )
        {
            return false;
        }

        return MatchesIdentityRoute(logEvent, "Path") || MatchesIdentityRoute(logEvent, "RequestPath");
    }

    private static bool MatchesIdentityRoute(LogEvent logEvent, string propertyName)
    {
        if (
            !logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? pathValue)
            || pathValue is not ScalarValue { Value: string path }
        )
        {
            return false;
        }

        // No configuration seam here - this is wired directly as a Serilog Func<LogEvent, bool> -
        // so the leading tenant/qualifier segment count is unconstrained.
        return IdentityRoutePathRedactor.IsIdentifierBearingPathWithAnyPrefix(path);
    }
}
