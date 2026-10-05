// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

/// <summary>One log call as a logger provider received it.</summary>
public sealed record RecordedLog(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> State,
    Exception? Exception
)
{
    /// <summary>Every text this record carries: category, message, structured values and any exception.</summary>
    public string AllText =>
        string.Join(
            '\n',
            new[] { Category, Message, Exception?.ToString() ?? "" }.Concat(
                State.Select(pair => $"{pair.Key}={pair.Value}")
            )
        );
}

/// <summary>Records every call of every category at every level, including <see cref="LogLevel.Trace"/>.</summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedLog> _records = new();

    public IReadOnlyList<RecordedLog> Records => [.. _records];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _records);

    public void Dispose() { }

    private sealed class RecordingLogger(string category, ConcurrentQueue<RecordedLog> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            records.Enqueue(
                new RecordedLog(
                    category,
                    logLevel,
                    formatter(state, exception),
                    state as IReadOnlyList<KeyValuePair<string, object?>> ?? [],
                    exception
                )
            );
    }
}
