// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal sealed class DocumentCacheAdminLogSanitizingTextFormatter : ITextFormatter
{
    public static DocumentCacheAdminLogSanitizingTextFormatter Instance { get; } = new();

    // The cdc verbs are allowlisted under their scoped labels, which is also how they are recorded: the
    // group's `status` verb and the DocumentCache `status` command would otherwise be one label in the
    // logs.
    private static readonly HashSet<string> SafeCommandLabels =
    [
        DocumentCacheAdminCommandSurface.StatusCommandName,
        DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
        DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
        DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
        DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
        DocumentCacheAdminCommandSurface.ScrubCommandName,
        DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
        .. DocumentCacheAdminCommandSurface.CdcVerbNames.Select(
            DocumentCacheAdminCommandSurface.CdcCommandLabel
        ),
    ];

    private static readonly (string PropertyName, string OutputName)[] SafeProperties =
    [
        ("Command", "command"),
        ("Target", "target"),
        ("OutputMode", "outputMode"),
        ("Outcome", "outcome"),
        ("Category", "category"),
        ("ExitCode", "exitCode"),
        ("DurationMs", "durationMs"),
    ];

    private DocumentCacheAdminLogSanitizingTextFormatter() { }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write(logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        Write(output, "level", logEvent.Level.ToString());

        if (TryGetScalarText(logEvent, "SourceContext", out string? sourceContext))
        {
            Write(output, "source", SafeBoundedValue(sourceContext));
        }

        Write(output, "event", SafeEventName(logEvent));

        foreach ((string propertyName, string outputName) in SafeProperties)
        {
            if (
                TryGetScalarText(logEvent, propertyName, out string? value)
                && TrySanitizeSafeProperty(propertyName, value, out string? safeValue)
            )
            {
                Write(output, outputName, safeValue);
            }
        }

        if (logEvent.Exception is not null)
        {
            Write(output, "exceptionType", logEvent.Exception.GetType().Name);
        }

        output.WriteLine();
    }

    private static bool TryGetScalarText(
        LogEvent logEvent,
        string propertyName,
        [NotNullWhen(true)] out string? value
    )
    {
        value = null;
        if (
            !logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? propertyValue)
            || propertyValue is not ScalarValue scalarValue
            || scalarValue.Value is null
        )
        {
            return false;
        }

        value = scalarValue.Value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : scalarValue.Value.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TrySanitizeSafeProperty(
        string propertyName,
        string value,
        [NotNullWhen(true)] out string? sanitizedValue
    )
    {
        sanitizedValue = propertyName switch
        {
            "Command" => SafeCommand(value),
            "Target" => SafeTarget(value),
            "OutputMode" => SafeOutputMode(value),
            "ExitCode" => SafeInteger(value),
            "DurationMs" => SafeDuration(value),
            _ => SafeBoundedValue(value),
        };

        return sanitizedValue is not null;
    }

    private static string? SafeCommand(string value) => SafeCommandLabels.Contains(value) ? value : null;

    private static string? SafeTarget(string value)
    {
        string sanitized = SafeBoundedValue(value);
        return sanitized.StartsWith("t1_", StringComparison.Ordinal) ? sanitized : null;
    }

    private static string? SafeOutputMode(string value) => value is "json" or "human" ? value : null;

    private static string? SafeInteger(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? SafeDuration(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? Math.Max(0, parsed).ToString("0.###", CultureInfo.InvariantCulture)
            : null;

    private static string SafeEventName(LogEvent logEvent)
    {
        string sanitizedTemplate = DocumentCacheAdminOutput.SanitizeDiagnostic(logEvent.MessageTemplate.Text);
        if (string.IsNullOrWhiteSpace(sanitizedTemplate) || sanitizedTemplate == "diagnostic redacted")
        {
            return "diagnosticRedacted";
        }

        string firstToken = sanitizedTemplate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string candidate = firstToken.TrimEnd('.', ':');
        if (candidate.Length == 0 || !candidate.All(char.IsLetterOrDigit))
        {
            return "event";
        }

        if (
            logEvent.Exception is not null
            && !candidate.StartsWith("DocumentCacheAdmin", StringComparison.Ordinal)
        )
        {
            return "exception";
        }

        return DocumentCacheAdminOutput.BoundedLabel(candidate);
    }

    private static string SafeBoundedValue(string value)
    {
        string sanitized = DocumentCacheAdminOutput.SanitizeDiagnostic(value);
        return sanitized == "diagnostic redacted"
            ? "redacted"
            : DocumentCacheAdminOutput.BoundedLabel(sanitized);
    }

    private static void Write(TextWriter output, string name, string value)
    {
        output.Write(' ');
        output.Write(name);
        output.Write(' ');
        output.Write(value);
    }
}
