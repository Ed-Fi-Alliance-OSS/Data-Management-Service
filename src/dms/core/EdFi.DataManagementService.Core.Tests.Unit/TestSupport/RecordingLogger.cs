// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Tests.Unit.TestSupport;

internal sealed class RecordingLogger : ILogger
{
    private readonly List<LogRecord> _records = [];

    public IReadOnlyList<LogRecord> Records => _records.ToArray();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Dictionary<string, object?> properties = state
            is IEnumerable<KeyValuePair<string, object?>> propertyValues
            ? propertyValues
                .Where(static propertyValue => propertyValue.Key != "{OriginalFormat}")
                .ToDictionary(
                    static propertyValue => propertyValue.Key,
                    static propertyValue => propertyValue.Value,
                    StringComparer.Ordinal
                )
            : [];

        _records.Add(new LogRecord(logLevel, formatter(state, exception), exception, properties));
    }
}

internal sealed record LogRecord(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties
);
