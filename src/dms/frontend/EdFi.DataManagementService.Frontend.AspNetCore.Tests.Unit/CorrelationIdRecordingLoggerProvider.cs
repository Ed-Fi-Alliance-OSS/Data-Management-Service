// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Captures the TraceId that LoggingMiddleware writes into its request log events, which
/// is the value an operator would search the logs for. Registered against a
/// WebApplicationFactory's logging builder by the fixtures that boot the real pipeline
/// in-process.
/// </summary>
internal sealed class CorrelationIdRecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _traceIds = new();

    public string[] LoggedTraceIds => [.. _traceIds];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(_traceIds);

    public void Dispose() { }

    private sealed class RecordingLogger(ConcurrentQueue<string> traceIds) : ILogger
    {
        // Scopes are not part of what this provider records, so the framework's own no-op
        // scope stands in rather than a private re-implementation of it.
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                return;
            }

            foreach (KeyValuePair<string, object?> value in values)
            {
                if (value.Key == "TraceId" && value.Value is string traceId)
                {
                    traceIds.Enqueue(traceId);
                }
            }
        }
    }
}
