// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// <see cref="ILogger{T}"/> double that captures each emitted log entry so integration tests can
/// pin exact level/message/exception combinations on FK-classification and delete-path logging
/// branches that traverse the real database driver.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogRecord> _records = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

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
        lock (_gate)
        {
            _records.Add(new LogRecord(logLevel, formatter(state, exception), exception));
        }
    }
}

internal sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);
