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
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

internal class TestLogger<T> : ILogger<T>
{
    public record LogEntry(
        LogLevel Level,
        EventId EventId,
        object? State,
        Exception? Exception,
        object?[] ActiveScopes
    );

    public readonly List<LogEntry> Entries = new();
    private readonly Func<LogLevel, bool> _isEnabled;

    private readonly Stack<object?> _scopes = new();
    private readonly List<object?> _createdScopes = new();

    public TestLogger()
        : this(_ => true) { }

    public TestLogger(Func<LogLevel, bool> isEnabled)
    {
        _isEnabled = isEnabled;
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        _scopes.Push(state);
        _createdScopes.Add(state);
        return new Scope(() => _scopes.Pop());
    }

    public IEnumerable<object?> Scopes => _createdScopes.ToList();

    public bool IsEnabled(LogLevel logLevel) => _isEnabled(logLevel);

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
internal class Given_RequestLoggingMiddleware
{
    private RequestDelegate _next = null!;

    [SetUp]
    public void Setup()
    {
        _next = _ => Task.CompletedTask;
    }

    [Test]
    public async Task It_logs_completion_information_and_scope_properties()
    {
        var nextCallCount = 0;
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/v1/test";
        httpContext.Response.StatusCode = 204;
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(context =>
        {
            context.Should().BeSameAs(httpContext);
            nextCallCount++;
            return Task.CompletedTask;
        });

        await middleware.Invoke(httpContext, logger);

        nextCallCount.Should().Be(1);
        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestCompleted");
        entry.EventId.Should().Be(RequestLoggingEventIds.HttpRequestCompleted);
        entry.Level.Should().Be(LogLevel.Information);
        entry.State.ContainStructuredProperty("EventName", "HttpRequestCompleted");
        entry
            .State.GetStructuredProperty("{OriginalFormat}")
            .Should()
            .Be(
                "{EventName}: CMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}"
            );

        var scope = logger.Scopes.Single();
        scope.ContainStructuredProperty("Application", "EdFi.DmsConfigurationService");
        scope.ContainStructuredProperty("TraceId", httpContext.TraceIdentifier);
        scope.ContainStructuredProperty("Method", "GET");
        scope.ContainStructuredProperty("Path", "/api/v1/test");
        scope.ContainStructuredProperty("PathBase", string.Empty);
        scope.ContainStructuredProperty("StatusCode", 204);
        scope
            .Should()
            .BeAssignableTo<IEnumerable<KeyValuePair<string, object?>>>()
            .Which.Any(kvp => kvp.Key == "DurationMs" && kvp.Value is long)
            .Should()
            .BeTrue("duration should be captured in the log scope");
    }

    [Test]
    public void It_logs_failure_status_code_and_rethrows_original_exception()
    {
        var exception = new InvalidOperationException("boom");
        RequestDelegate failingNext = _ => throw exception;
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/v1/test";
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(failingNext);

        Action act = () => middleware.Invoke(httpContext, logger).GetAwaiter().GetResult();

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(exception);
        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestFailed");
        entry.EventId.Should().Be(RequestLoggingEventIds.HttpRequestFailed);
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(exception);
        entry.State.ContainStructuredProperty("EventName", "HttpRequestFailed");
        entry
            .State.GetStructuredProperty("{OriginalFormat}")
            .Should()
            .Be(
                "{EventName}: CMS request failed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}"
            );
        logger.Scopes.Should().Contain(scope => scope.HasStructuredProperty("Method", "GET"));
        logger.Scopes.Should().Contain(scope => scope.HasStructuredProperty("Path", "/api/v1/test"));
        logger.Scopes.Should().Contain(scope => scope.HasStructuredProperty("StatusCode", 500));
        logger
            .Scopes.Should()
            .Contain(scope => scope.HasStructuredProperty("TraceId", httpContext.TraceIdentifier));
        logger.Scopes.Should().Contain(scope => scope.HasStructuredProperty("PathBase", string.Empty));
    }

    [Test]
    public async Task It_logs_well_known_path_at_debug()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/.well-known/openid-configuration";
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(_next);

        await middleware.Invoke(httpContext, logger);

        var entry = logger.Entries.Single(e => e.EventId.Name == "HttpRequestCompleted");
        entry.Level.Should().Be(LogLevel.Debug);
    }

    [Test]
    public async Task It_skips_well_known_completion_logging_when_debug_is_disabled_but_keeps_request_scope()
    {
        var nextCallCount = 0;
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/.well-known/openid-configuration";
        var logger = new TestLogger<RequestLoggingMiddleware>(level => level != LogLevel.Debug);
        var middleware = new RequestLoggingMiddleware(_ =>
        {
            nextCallCount++;
            return Task.CompletedTask;
        });

        await middleware.Invoke(httpContext, logger);

        nextCallCount.Should().Be(1);
        logger.Entries.Should().BeEmpty();
        logger
            .Scopes.Should()
            .ContainSingle(scope =>
                scope.HasStructuredProperty("Application", "EdFi.DmsConfigurationService")
            );
    }

    [Test]
    public void It_keeps_request_logging_event_ids_in_sync_with_the_local_contract()
    {
        RequestLoggingEventIds.HttpRequestCompleted.Should().Be(new EventId(1228001, "HttpRequestCompleted"));
        RequestLoggingEventIds.HttpRequestFailed.Should().Be(new EventId(1228002, "HttpRequestFailed"));
    }

    [Test]
    public void It_sanitizes_values_using_the_local_request_logging_whitelist()
    {
        var samples = new (string? Input, string Expected)[]
        {
            (null, string.Empty),
            (string.Empty, string.Empty),
            ("GET /ed-fi/students", "GET /ed-fi/students"),
            ("trace\r\nid\twith{unsafe}", "traceidwithunsafe"),
            ("tenant_name-1.2:3\\path/segment", "tenant_name-1.2:3\\path/segment"),
            ("unsafe\"quote'and?query=value", "unsafequoteandqueryvalue"),
        };

        foreach (var sample in samples)
        {
            LoggingUtility.SanitizeForLog(sample.Input).Should().Be(sample.Expected);
        }
    }

    [Test]
    public async Task It_adds_activity_trace_and_span_ids_to_request_scope()
    {
        using var activity = new Activity("request-log-test");
        activity.Start();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/v1/test";
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(_next);

        await middleware.Invoke(httpContext, logger);

        var scope = logger.Scopes.Single();
        scope.ContainStructuredProperty("ActivityTraceId", activity.TraceId.ToString());
        scope.ContainStructuredProperty("SpanId", activity.SpanId.ToString());
    }

    [Test]
    public async Task It_logs_non_empty_path_base_in_request_scope()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/management";
        httpContext.Request.Path = "/api/v1/test";
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(_next);

        await middleware.Invoke(httpContext, logger);

        var scope = logger.Scopes.Single();
        scope.ContainStructuredProperty("PathBase", "/management");
    }

    [Test]
    public async Task It_logs_downstream_500_response_as_failure()
    {
        var httpContext = new DefaultHttpContext();
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });

        await middleware.Invoke(httpContext, logger);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Name == "HttpRequestFailed").Subject;
        entry.EventId.Should().Be(RequestLoggingEventIds.HttpRequestFailed);
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeNull();
        entry.State.ContainStructuredProperty("EventName", "HttpRequestFailed");
        logger
            .Scopes.Should()
            .Contain(scope =>
                scope.HasStructuredProperty("StatusCode", StatusCodes.Status500InternalServerError)
            );
    }

    [Test]
    public async Task It_attaches_exception_handler_feature_exception_to_downstream_500_failure()
    {
        var exception = new InvalidOperationException("handled by exception handler");
        var httpContext = new DefaultHttpContext();
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(context =>
        {
            context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = exception });
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });

        await middleware.Invoke(httpContext, logger);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Name == "HttpRequestFailed").Subject;
        entry.Exception.Should().BeSameAs(exception);
    }

    [Test]
    public void It_logs_500_for_unhandled_exceptions_before_response_starts()
    {
        var exception = new InvalidOperationException("boom");
        RequestDelegate failingNext = context =>
        {
            context.Response.StatusCode = StatusCodes.Status418ImATeapot;
            throw exception;
        };
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/v1/test";
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(failingNext);

        Action act = () => middleware.Invoke(httpContext, logger).GetAwaiter().GetResult();

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(exception);
        logger
            .Scopes.Should()
            .Contain(scope =>
                scope.HasStructuredProperty("StatusCode", StatusCodes.Status500InternalServerError)
            );
    }

    [Test]
    public async Task It_excludes_request_body_authorization_header_and_raw_query_string_from_log_state()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/v1/test";
        httpContext.Request.QueryString = new QueryString("?clientSecret=secret-query-value");
        httpContext.Request.Headers.Authorization = "Bearer secret-token-value";
        httpContext.Request.Body = new MemoryStream("secret-body-value"u8.ToArray());
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(_next);

        await middleware.Invoke(httpContext, logger);

        var loggedValues = logger.Entries.Select(e => e.State).Concat(logger.Scopes).StructuredValues();
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
    public async Task It_sanitizes_trace_identifier_before_logging()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "trace\r\nid\twith{unsafe}";
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(_next);

        await middleware.Invoke(httpContext, logger);

        var expectedTraceId = "traceidwithunsafe";
        var entry = logger.Entries.Single(e => e.EventId == RequestLoggingEventIds.HttpRequestCompleted);
        entry.State.ContainStructuredProperty("TraceId", expectedTraceId);
        var scope = logger.Scopes.Single();
        scope.ContainStructuredProperty("TraceId", expectedTraceId);
    }

    [Test]
    public async Task It_applies_request_scope_to_downstream_logs()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "cms-trace-id";
        httpContext.Request.Method = "POST";
        httpContext.Request.PathBase = "/management";
        httpContext.Request.Path = "/api/v1/test";
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(_ =>
        {
            logger.LogInformation("Downstream handler log");
            return Task.CompletedTask;
        });

        await middleware.Invoke(httpContext, logger);

        var downstreamEntry = logger.Entries.Single(e =>
            e.EventId.Id == 0
            && e.State.GetStructuredProperty("{OriginalFormat}")?.ToString() == "Downstream handler log"
        );
        downstreamEntry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("Application", "EdFi.DmsConfigurationService"));
        downstreamEntry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("TraceId", "cms-trace-id"));
        downstreamEntry.ActiveScopes.Should().Contain(scope => scope.HasStructuredProperty("Method", "POST"));
        downstreamEntry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("Path", "/api/v1/test"));
        downstreamEntry
            .ActiveScopes.Should()
            .Contain(scope => scope.HasStructuredProperty("PathBase", "/management"));
    }

    [Test]
    public void It_serializes_scope_and_template_properties_in_serilog_json_output()
    {
        var sink = new CapturingSerilogSink();
        using var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger);
        var logger = loggerFactory.CreateLogger("CmsRequestLogJsonContractTest");
        using var scope = logger.BeginScope(
            new Dictionary<string, object>
            {
                ["Application"] = "EdFi.DmsConfigurationService",
                ["TraceId"] = "json-trace-id",
                ["Method"] = "GET",
                ["Path"] = "/v3/vendors",
                ["PathBase"] = "",
            }
        );

        logger.Log(
            LogLevel.Information,
            RequestLoggingEventIds.HttpRequestCompleted,
            "{EventName}: CMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
            RequestLoggingEventIds.HttpRequestCompleted.Name,
            "GET",
            "/v3/vendors",
            200,
            42L,
            "json-trace-id"
        );

        using var writer = new StringWriter();
        new JsonFormatter(renderMessage: true).Format(sink.Events.Single(), writer);
        var json = JsonNode.Parse(writer.ToString());
        var properties = json?["Properties"];

        properties?["Application"]?.GetValue<string>().Should().Be("EdFi.DmsConfigurationService");
        properties?["EventName"]?.GetValue<string>().Should().Be("HttpRequestCompleted");
        properties?["TraceId"]?.GetValue<string>().Should().Be("json-trace-id");
        properties?["Method"]?.GetValue<string>().Should().Be("GET");
        properties?["Path"]?.GetValue<string>().Should().Be("/v3/vendors");
        properties?["StatusCode"]?.GetValue<int>().Should().Be(200);
        properties?["DurationMs"]?.GetValue<long>().Should().Be(42L);
        json?["RenderedMessage"]?.GetValue<string>().Should().Contain("CMS request completed");
    }

    [Test]
    public void It_configures_console_sink_with_serilog_json_formatter()
    {
        var appsettingsPath = FindRepositoryFile(
            "src",
            "config",
            "frontend",
            "EdFi.DmsConfigurationService.Frontend.AspNetCore",
            "appsettings.json"
        );
        var json = JsonNode.Parse(File.ReadAllText(appsettingsPath));
        var writeTo = json?["Serilog"]?["WriteTo"]?.AsArray();

        writeTo.Should().NotBeNull();
        writeTo!.Any(sink => IsConsoleJsonFormatterSink(sink)).Should().BeTrue();
    }

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
        state.Should().BeAssignableTo<IEnumerable<KeyValuePair<string, object?>>>();
        var values = (IEnumerable<KeyValuePair<string, object?>>)state!;

        values.Should().Contain(kvp => kvp.Key == propertyName && Equals(kvp.Value, expectedValue));
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
