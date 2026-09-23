// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using Serilog.Events;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Suppresses the framework's own <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> request-start and
/// request-finish log events for an identity get-by-id or results-poll route, because those events
/// carry the identifier verbatim in both their <c>Path</c> and <c>RequestPath</c> properties and in
/// the rendered message (D11). Every other route, including the other four identity routes (none of
/// which carries an identifier), keeps its framework events unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Wired into <see cref="LoggingConfigurator.ConfigureLogging"/> as
/// <c>.Filter.ByExcluding(IdentityHostingDiagnosticsFilter.Matches)</c>, so <see cref="Matches"/>
/// must have exactly the <c>Func&lt;LogEvent, bool&gt;</c> shape Serilog's filter expects.
/// </para>
/// <para>
/// Grounded in Task 4's probe (Contract round, Probe (a)): the request-starting (EventId 1) and
/// request-finished (EventId 2) events, both Information, carry both <c>Path</c> and
/// <c>RequestPath</c> simultaneously holding the identical value, so this checks both rather than
/// picking one. A third, unrelated event on the same <c>SourceContext</c>
/// (<c>HostingStartupAssemblyLoaded</c>, Debug, EventId 13) carries neither property, so every
/// property read here is guarded by a presence check before it is compared - an unguarded indexer
/// read would throw or, if defaulted, misfire on that unrelated event.
/// </para>
/// </remarks>
internal static class IdentityHostingDiagnosticsFilter
{
    private const string HostingDiagnosticsSourceContext = "Microsoft.AspNetCore.Hosting.Diagnostics";

    // Matches an identity get-by-id path (.../identity/v2/identities/{value}, excluding the literal
    // find and search operation names) or a results-poll path
    // (.../identity/v2/identities/results/{value}), with any number of leading path segments
    // (tenant and route-qualifier prefixes) ahead of /identity/v2. Unlike
    // IdentityRoutePathRedactor, this predicate has no configuration seam - it is wired directly as
    // a Serilog Func&lt;LogEvent, bool&gt; - so the leading-segment count is unconstrained rather
    // than pinned to the configured tenant/qualifier count.
    private static readonly Regex _identityIdOrTokenRouteRegex = new(
        @"^(?:/[^/]+)*/identity/v2/identities/(?:(?!find$|search$)[^/]+|results/[^/]+)$",
        RegexOptions.Compiled
    );

    public static bool Matches(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (
            !logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContextValue)
            || sourceContextValue is not ScalarValue { Value: string sourceContext }
            || !string.Equals(sourceContext, HostingDiagnosticsSourceContext, StringComparison.Ordinal)
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

        return _identityIdOrTokenRouteRegex.IsMatch(path);
    }
}
