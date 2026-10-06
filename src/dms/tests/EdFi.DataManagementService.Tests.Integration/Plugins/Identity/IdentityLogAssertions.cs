// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// Assertions over captured log events for the identity redaction cases.
/// </summary>
/// <remarks>
/// Ported from the frontend unit tests' <c>IdentityLogRedactionTests</c> so the plugin path is held
/// to the same standard as the host default. The sanitizer strips braces, so the logged identity
/// path carries no template literal; callers assert the identifier is absent rather than matching a
/// template.
/// </remarks>
internal static class IdentityLogAssertions
{
    public const string HostingDiagnosticsSourceContext = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public static string? ScalarProperty(LogEvent logEvent, string propertyName)
    {
        if (
            !logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? value)
            || value is not ScalarValue scalar
        )
        {
            return null;
        }

        return scalar.Value?.ToString();
    }

    /// <summary>The frontend layer's completion event, whether the request succeeded or failed.</summary>
    public static bool IsFrontendCompletionEvent(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text.Contains("DMS request completed", StringComparison.Ordinal)
        || logEvent.MessageTemplate.Text.Contains("DMS request failed", StringComparison.Ordinal);

    /// <summary>The Core layer's completion event, whether the request succeeded or failed.</summary>
    public static bool IsCoreCompletionEvent(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text.Contains("DMS core request completed", StringComparison.Ordinal)
        || logEvent.MessageTemplate.Text.Contains("DMS core request failed", StringComparison.Ordinal);

    /// <summary>
    /// Positive control: the framework's hosting-diagnostics events whose <c>Path</c> is exactly
    /// <paramref name="path"/>. A leak scan over a capture that never held these events proves
    /// nothing, so a case scanning hosting events asserts this finds some for a route that is not
    /// redacted.
    /// </summary>
    public static IReadOnlyList<LogEvent> HostingDiagnosticsEventsForPath(
        IEnumerable<LogEvent> events,
        string path
    ) =>
        [
            .. events.Where(logEvent =>
                ScalarProperty(logEvent, "SourceContext") == HostingDiagnosticsSourceContext
                && ScalarProperty(logEvent, "Path") == path
            ),
        ];

    /// <summary>
    /// Describes every captured event that carries <paramref name="secret"/> anywhere a sink could
    /// write it: the rendered message, or any scalar property value, including scalars nested inside
    /// structure, sequence, or dictionary property values. Empty when nothing leaks.
    /// </summary>
    public static IReadOnlyList<string> EventsLeaking(IEnumerable<LogEvent> events, string secret) =>
        events
            .SelectMany(logEvent =>
                logEvent
                    .Properties.Where(property => ValueContains(property.Value, secret))
                    .Select(property =>
                        $"[{ScalarProperty(logEvent, "SourceContext")}] {logEvent.MessageTemplate.Text} :: property {property.Key}"
                    )
                    .Concat(
                        logEvent.RenderMessage().Contains(secret, StringComparison.Ordinal)
                            ?
                            [
                                $"[{ScalarProperty(logEvent, "SourceContext")}] {logEvent.MessageTemplate.Text} :: rendered message",
                            ]
                            : []
                    )
            )
            .ToList();

    /// <summary>
    /// <see cref="EventsLeaking"/> plus the attached exception: its type, message, stack trace and
    /// every inner exception, which is how a provider's exception text would reach a sink that renders
    /// exceptions.
    /// </summary>
    public static IReadOnlyList<string> EventsCarrying(IEnumerable<LogEvent> events, string secret)
    {
        List<LogEvent> captured = [.. events];

        return
        [
            .. EventsLeaking(captured, secret),
            .. captured
                .Where(logEvent =>
                    logEvent.Exception?.ToString().Contains(secret, StringComparison.Ordinal) ?? false
                )
                .Select(logEvent =>
                    $"[{ScalarProperty(logEvent, "SourceContext")}] {logEvent.MessageTemplate.Text} :: exception"
                ),
        ];
    }

    public static bool ValueContains(LogEventPropertyValue value, string secret) =>
        value switch
        {
            ScalarValue scalar => scalar.Value?.ToString()?.Contains(secret, StringComparison.Ordinal)
                ?? false,
            StructureValue structure => structure.Properties.Any(p => ValueContains(p.Value, secret)),
            SequenceValue sequence => sequence.Elements.Any(element => ValueContains(element, secret)),
            DictionaryValue dictionary => dictionary.Elements.Any(pair =>
                ValueContains(pair.Key, secret) || ValueContains(pair.Value, secret)
            ),
            _ => value.ToString().Contains(secret, StringComparison.Ordinal),
        };
}
