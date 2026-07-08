// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EdFi.DataManagementService.Core.External.Logging;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class TestLogger<T> : ILogger<T>
{
    public record LogEntry(
        LogLevel Level,
        EventId EventId,
        object? State,
        Exception? Exception,
        object?[] ActiveScopes
    );

    internal readonly List<LogEntry> Entries = new();
    private readonly List<object?> _createdScopes = new();
    private readonly Stack<object?> _scopes = new();

    IDisposable ILogger.BeginScope<TState>(TState state)
    {
        _createdScopes.Add(state);
        _scopes.Push(state);
        return new Scope(() => _scopes.Pop());
    }

    public IEnumerable<object?> Scopes => _createdScopes.ToArray();

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        Entries.Add(new LogEntry(logLevel, eventId, state!, exception, _scopes.ToArray()));
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _onDispose;

        public Scope(Action onDispose) => _onDispose = onDispose;

        public void Dispose() => _onDispose();
    }
}

[TestFixture]
[Parallelizable]
public class Given_LoggingMiddleware
{
    private RequestDelegate _next = null!;

    [SetUp]
    public void Setup()
    {
        _next = _ => Task.CompletedTask;
    }

    [Test]
    public async Task It_logs_completion_using_the_dms_request_contract()
    {
        var nextCallCount = 0;
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/ed-fi/students";
        httpContext.Request.Headers["x-correlation-id"] = "operator-trace-id";
        httpContext.Response.StatusCode = 200;
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(
            context =>
            {
                context.Should().BeSameAs(httpContext);
                nextCallCount++;
                return Task.CompletedTask;
            },
            AppSettingsWithCorrelationHeader("x-correlation-id")
        );

        await middleware.Invoke(httpContext, logger);

        nextCallCount.Should().Be(1);
        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestCompleted");
        entry.Level.Should().Be(LogLevel.Information);
        entry.State.ContainStructuredProperty("EventName", "HttpRequestCompleted");
        entry
            .State.GetStructuredProperty("{OriginalFormat}")
            .Should()
            .Be(
                "{EventName}: DMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}"
            );

        entry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("TraceId", "operator-trace-id"));
        entry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("RequestLayer", "Frontend"));
        entry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("Method", "GET"));
        entry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("Path", "/ed-fi/students"));
        entry.State.ContainStructuredProperty("StatusCode", 200);
        entry.State.ContainKey("DurationMs");
    }

    [Test]
    public async Task It_logs_request_start_at_debug_so_information_request_events_follow_the_contract()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/ed-fi/students";
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(_next, AppSettingsWithCorrelationHeader(string.Empty));

        await middleware.Invoke(httpContext, logger);

        logger
            .Entries.Should()
            .ContainSingle(e =>
                e.Level == LogLevel.Debug
                && e.State.HasStructuredProperty("{OriginalFormat}", "Request started")
            );
    }

    [Test]
    public void It_logs_failure_using_the_dms_request_contract_and_preserves_wrapping_behavior()
    {
        var exception = new InvalidOperationException("boom");
        RequestDelegate failingNext = _ => throw exception;
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/ed-fi/students";
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(failingNext, AppSettingsWithCorrelationHeader(string.Empty));

        Action act = () => middleware.Invoke(httpContext, logger).GetAwaiter().GetResult();

        act.Should().Throw<InvalidOperationException>().Which.InnerException.Should().BeSameAs(exception);
        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestFailed");
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(exception);
        entry.State.ContainStructuredProperty("EventName", "HttpRequestFailed");
        entry
            .State.GetStructuredProperty("{OriginalFormat}")
            .Should()
            .Be(
                "{EventName}: DMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}"
            );
        entry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("Application", "EdFi.DataManagementService"));
        entry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("RequestLayer", "Frontend"));
        entry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("Method", "GET"));
        entry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("Path", "/ed-fi/students"));
        entry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("PathBase", string.Empty));
        entry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("TraceId", httpContext.TraceIdentifier));
        entry.State.ContainStructuredProperty("StatusCode", 500);
        entry.State.ContainKey("DurationMs");
    }

    [Test]
    public void It_logs_500_for_unhandled_exceptions_before_the_response_starts()
    {
        var exception = new InvalidOperationException("boom");
        RequestDelegate failingNext = context =>
        {
            context.Response.StatusCode = StatusCodes.Status418ImATeapot;
            throw exception;
        };
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/ed-fi/students";
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(failingNext, AppSettingsWithCorrelationHeader(string.Empty));

        Action act = () => middleware.Invoke(httpContext, logger).GetAwaiter().GetResult();

        act.Should().Throw<InvalidOperationException>().Which.InnerException.Should().BeSameAs(exception);

        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestFailed");
        entry.State.ContainStructuredProperty("StatusCode", StatusCodes.Status500InternalServerError);
        entry.State.ContainKey("DurationMs");
    }

    [Test]
    public async Task It_adds_activity_trace_and_span_ids_to_request_scope()
    {
        using var activity = new Activity("dms-request-log-test");
        activity.Start();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/ed-fi/students";
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(_next, AppSettingsWithCorrelationHeader(string.Empty));

        await middleware.Invoke(httpContext, logger);

        logger
            .Scopes.Should()
            .Contain(scope => scope.HasStructuredProperty("ActivityTraceId", activity.TraceId.ToString()));
        logger
            .Scopes.Should()
            .Contain(scope => scope.HasStructuredProperty("SpanId", activity.SpanId.ToString()));
    }

    [Test]
    public async Task It_logs_non_empty_path_base_in_request_scope()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/data";
        httpContext.Request.Path = "/ed-fi/students";
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(_next, AppSettingsWithCorrelationHeader(string.Empty));

        await middleware.Invoke(httpContext, logger);

        logger.Scopes.Should().Contain(scope => scope.HasStructuredProperty("PathBase", "/data"));
    }

    [Test]
    public async Task It_falls_back_to_trace_identifier_when_correlation_settings_are_invalid()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "trace\r\nid\twith{unsafe}";
        httpContext.Request.Path = "/ed-fi/students";
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(
            _next,
            new ThrowingOptions<AppSettings>(
                new OptionsValidationException(
                    nameof(AppSettings),
                    typeof(AppSettings),
                    ["CorrelationIdHeader"]
                )
            )
        );

        await middleware.Invoke(httpContext, logger);

        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestCompleted");
        entry.State.ContainStructuredProperty("TraceId", "traceidwithunsafe");
        entry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("TraceId", "traceidwithunsafe"));
    }

    [Test]
    public async Task It_logs_downstream_500_response_as_failure()
    {
        var httpContext = new DefaultHttpContext();
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            },
            AppSettingsWithCorrelationHeader(string.Empty)
        );

        await middleware.Invoke(httpContext, logger);

        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestFailed");
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeNull();
        entry.State.ContainStructuredProperty("EventName", "HttpRequestFailed");
        entry.State.ContainStructuredProperty("StatusCode", StatusCodes.Status500InternalServerError);
        entry.State.ContainKey("DurationMs");
        logger.Entries.Should().NotContain(e => e.EventId.Name == "HttpRequestCompleted");
    }

    [Test]
    public async Task It_leaves_request_body_too_large_rejections_at_413()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/schemas";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(
            _ =>
                throw new BadHttpRequestException(
                    "Request body too large.",
                    StatusCodes.Status413PayloadTooLarge
                ),
            AppSettingsWithCorrelationHeader(string.Empty)
        );

        await FluentActions.Awaiting(() => middleware.Invoke(httpContext, logger)).Should().NotThrowAsync();

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        responseBody.ToArray().Should().BeEmpty();
        logger
            .Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Exception is BadHttpRequestException);
    }

    [Test]
    public async Task It_keeps_normal_exceptions_on_the_existing_500_path()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/schemas";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(
            _ => throw new InvalidOperationException("boom"),
            AppSettingsWithCorrelationHeader(string.Empty)
        );

        Func<Task> invoke = () => middleware.Invoke(httpContext, logger);

        await invoke.Should().ThrowAsync<InvalidOperationException>();

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        httpContext.Response.ContentType.Should().Be("application/json");
        responseBody.ToArray().Should().NotBeEmpty();
    }

    [Test]
    public async Task It_excludes_request_body_authorization_header_and_raw_query_string_from_log_state()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/ed-fi/students";
        httpContext.Request.QueryString = new QueryString("?clientSecret=secret-query-value");
        httpContext.Request.Headers.Authorization = "Bearer secret-token-value";
        httpContext.Request.Body = new MemoryStream("secret-body-value"u8.ToArray());
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(_next, AppSettingsWithCorrelationHeader(string.Empty));

        await middleware.Invoke(httpContext, logger);

        var loggedValues = logger
            .Entries.SelectMany(e => new object?[] { e.State }.Concat(e.ActiveScopes))
            .Concat(logger.Scopes)
            .StructuredValues();
        loggedValues
            .Should()
            .NotContain(value => value.Contains("secret-query-value", StringComparison.Ordinal));
        loggedValues
            .Should()
            .NotContain(value => value.Contains("secret-token-value", StringComparison.Ordinal));
        loggedValues
            .Should()
            .NotContain(value => value.Contains("secret-body-value", StringComparison.Ordinal));
    }

    [Test]
    public void It_serializes_scope_and_template_properties_in_serilog_json_output()
    {
        var sink = new CapturingSerilogSink();
        using var serilogLogger = new Serilog.LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger);
        var logger = loggerFactory.CreateLogger("DmsFrontendRequestLogJsonContractTest");
        using var scope = logger.BeginScope(
            new Dictionary<string, object>
            {
                ["Application"] = "EdFi.DataManagementService",
                ["TraceId"] = "json-trace-id",
                ["RequestLayer"] = "Frontend",
                ["Method"] = "GET",
                ["Path"] = "/ed-fi/students",
                ["PathBase"] = "",
            }
        );

        logger.Log(
            LogLevel.Information,
            RequestLoggingEventIds.HttpRequestCompleted,
            "{EventName}: DMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
            RequestLoggingEventIds.HttpRequestCompleted.Name,
            "GET",
            "/ed-fi/students",
            200,
            42L,
            "json-trace-id"
        );

        using var writer = new StringWriter();
        new JsonFormatter(renderMessage: true).Format(sink.Events.Single(), writer);
        var json = JsonNode.Parse(writer.ToString());
        var properties = json?["Properties"];

        properties?["Application"]?.GetValue<string>().Should().Be("EdFi.DataManagementService");
        properties?["EventName"]?.GetValue<string>().Should().Be("HttpRequestCompleted");
        properties?["TraceId"]?.GetValue<string>().Should().Be("json-trace-id");
        properties?["RequestLayer"]?.GetValue<string>().Should().Be("Frontend");
        properties?["Method"]?.GetValue<string>().Should().Be("GET");
        properties?["Path"]?.GetValue<string>().Should().Be("/ed-fi/students");
        properties?["StatusCode"]?.GetValue<int>().Should().Be(200);
        properties?["DurationMs"]?.GetValue<long>().Should().Be(42L);
        json?["RenderedMessage"]?.GetValue<string>().Should().Contain("DMS request completed");
    }

    [Test]
    public void It_configures_console_sink_with_serilog_json_formatter()
    {
        var appsettingsPath = FindRepositoryFile(
            "src",
            "dms",
            "frontend",
            "EdFi.DataManagementService.Frontend.AspNetCore",
            "appsettings.json"
        );
        var json = JsonNode.Parse(File.ReadAllText(appsettingsPath));
        var writeTo = json?["Serilog"]?["WriteTo"]?.AsArray();

        writeTo.Should().NotBeNull();
        writeTo!.Any(sink => IsConsoleJsonFormatterSink(sink)).Should().BeTrue();
    }

    private static IOptions<AppSettings> AppSettingsWithCorrelationHeader(string correlationHeader) =>
        Options.Create(
            new AppSettings
            {
                AuthenticationService = "http://localhost/connect/token",
                Datastore = "postgresql",
                CorrelationIdHeader = correlationHeader,
            }
        );

    private static string FindRepositoryFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find repository file.", Path.Combine(pathParts));
    }

    private static bool IsConsoleJsonFormatterSink(JsonNode? sink)
    {
        if (sink is null || sink["Name"]?.GetValue<string>() != "Console")
        {
            return false;
        }

        var formatter = sink["Args"]?["formatter"];
        if (formatter is null)
        {
            return false;
        }

        // Handle both string format (legacy) and object format (new)
        if (formatter.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            var formatterString = formatter.GetValue<string>();
            return formatterString == "Serilog.Formatting.Json.JsonFormatter, Serilog";
        }

        if (formatter is JsonObject formatterObj)
        {
            var type = formatterObj["type"]?.GetValue<string>();
            var renderMessage = formatterObj["renderMessage"]?.GetValue<bool>();
            return type == "Serilog.Formatting.Json.JsonFormatter, Serilog" && renderMessage == true;
        }

        return false;
    }

    private sealed class ThrowingOptions<T>(OptionsValidationException exception) : IOptions<T>
        where T : class
    {
        public T Value => throw exception;
    }

    private sealed class CapturingSerilogSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent)
        {
            _events.Add(logEvent);
        }
    }
}

internal static class LogStateAssertions
{
    public static void ContainStructuredProperty<TValue>(
        this object? state,
        string propertyName,
        TValue expectedValue
    )
    {
        state.HasStructuredProperty(propertyName, expectedValue).Should().BeTrue();
    }

    public static void ContainKey(this object? state, string propertyName)
    {
        state.Should().BeAssignableTo<IEnumerable<KeyValuePair<string, object?>>>();
        var values = (IEnumerable<KeyValuePair<string, object?>>)state!;

        values.Should().Contain(kvp => kvp.Key == propertyName);
    }

    public static bool HasStructuredProperty<TValue>(
        this object? state,
        string propertyName,
        TValue expectedValue
    )
    {
        return state is IEnumerable<KeyValuePair<string, object?>> values
            && values.Any(kvp => kvp.Key == propertyName && Equals(kvp.Value, expectedValue));
    }

    public static bool HasLongStructuredProperty(this object? state, string propertyName)
    {
        return state is IEnumerable<KeyValuePair<string, object?>> values
            && values.Any(kvp => kvp.Key == propertyName && kvp.Value is long);
    }

    public static object? GetStructuredProperty(this object? state, string propertyName)
    {
        state.Should().BeAssignableTo<IEnumerable<KeyValuePair<string, object?>>>();
        var values = (IEnumerable<KeyValuePair<string, object?>>)state!;

        return values.Single(kvp => kvp.Key == propertyName).Value;
    }

    public static IEnumerable<string> StructuredValues(this IEnumerable<object?> states)
    {
        return states
            .OfType<IEnumerable<KeyValuePair<string, object?>>>()
            .SelectMany(state => state)
            .Select(kvp => kvp.Value?.ToString() ?? string.Empty);
    }
}
