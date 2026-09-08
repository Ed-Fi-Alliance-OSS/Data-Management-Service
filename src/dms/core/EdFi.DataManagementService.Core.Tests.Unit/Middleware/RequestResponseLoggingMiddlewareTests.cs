// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Logging;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class Given_RequestResponseLoggingMiddleware
{
    private RecordingLogger _logger = null!;
    private RequestResponseLoggingMiddleware _middleware = null!;

    [SetUp]
    public void Setup()
    {
        _logger = new RecordingLogger();
        _middleware = new RequestResponseLoggingMiddleware(_logger);
    }

    [Test]
    public async Task It_logs_completion_using_the_dms_request_contract()
    {
        var requestInfo = No.RequestInfo("core-trace-id");
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Path = "/ed-fi/students" };
        requestInfo.FrontendResponse = new FrontendResponse(201, Body: null, Headers: []);

        await _middleware.Execute(requestInfo, TestHelper.NullNext);

        var record = _logger.Records.Single(log => log.EventId.Name == "HttpRequestCompleted");
        record.Level.Should().Be(LogLevel.Information);
        record.Properties.Should().Contain("EventName", "HttpRequestCompleted");
        record.Properties.Should().Contain("Method", "GET");
        record.Properties.Should().Contain("Path", "/ed-fi/students");
        record.Properties.Should().Contain("StatusCode", 201);
        record.Properties.Should().Contain("TraceId", "core-trace-id");
        record.Properties.Should().ContainKey("DurationMs");
        record.Properties["DurationMs"].Should().BeOfType<long>();
        record.Properties.Should().NotContainKey("Duration");
        record.ActiveScopes.Should().ContainSingle();
        var scope = record.ActiveScopes.Single();
        scope.Should().Contain("Application", "EdFi.DataManagementService");
        scope.Should().Contain("RequestLayer", "Core");
        scope.Should().Contain("TraceId", "core-trace-id");
        scope.Should().Contain("Method", "GET");
        scope.Should().Contain("Path", "/ed-fi/students");
        scope.Should().NotContainKey("PathBase");
    }

    [Test]
    public async Task It_logs_request_start_as_debug_breadcrumb_only()
    {
        var requestInfo = No.RequestInfo("core-trace-id");
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Path = "/ed-fi/students" };
        requestInfo.FrontendResponse = new FrontendResponse(200, Body: null, Headers: []);

        await _middleware.Execute(requestInfo, TestHelper.NullNext);

        _logger
            .Records.Should()
            .ContainSingle(log =>
                log.Level == LogLevel.Debug
                && log.Message == "Core pipeline started: GET /ed-fi/students - TraceId: core-trace-id"
            );
        _logger
            .Records.Should()
            .NotContain(log =>
                log.Level == LogLevel.Information
                && log.Message == "Core pipeline started: GET /ed-fi/students - TraceId: core-trace-id"
            );
    }

    [Test]
    public void It_logs_failure_using_the_dms_request_contract_and_preserves_wrapping_behavior()
    {
        var exception = new InvalidOperationException("boom");
        var requestInfo = No.RequestInfo("core-trace-id");
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Path = "/ed-fi/students" };

        Action act = () => _middleware.Execute(requestInfo, () => throw exception).GetAwaiter().GetResult();

        act.Should().Throw<InvalidOperationException>().Which.InnerException.Should().BeSameAs(exception);
        var record = _logger.Records.Single(log => log.EventId.Name == "HttpRequestFailed");
        record.Level.Should().Be(LogLevel.Error);
        record.Exception.Should().BeSameAs(exception);
        record.Properties.Should().Contain("EventName", "HttpRequestFailed");
        record.Properties.Should().Contain("Method", "GET");
        record.Properties.Should().Contain("Path", "/ed-fi/students");
        record.Properties.Should().Contain("StatusCode", 500);
        record.Properties.Should().Contain("TraceId", "core-trace-id");
        record.Properties.Should().ContainKey("DurationMs");
        record.Properties["DurationMs"].Should().BeOfType<long>();
        record.Properties.Should().NotContainKey("Duration");
        record.ActiveScopes.Should().ContainSingle();
        var scope = record.ActiveScopes.Single();
        scope.Should().Contain("Application", "EdFi.DataManagementService");
        scope.Should().Contain("RequestLayer", "Core");
        scope.Should().Contain("TraceId", "core-trace-id");
        scope.Should().Contain("Method", "GET");
        scope.Should().Contain("Path", "/ed-fi/students");
        scope.Should().NotContainKey("PathBase");
    }

    [Test]
    public async Task It_logs_5xx_responses_as_request_failures()
    {
        var requestInfo = No.RequestInfo("core-trace-id");
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Path = "/ed-fi/students" };
        requestInfo.FrontendResponse = new FrontendResponse(503, Body: null, Headers: []);

        await _middleware.Execute(requestInfo, TestHelper.NullNext);

        var record = _logger.Records.Single(log => log.EventId.Name == "HttpRequestFailed");
        record.Level.Should().Be(LogLevel.Error);
        record.Exception.Should().BeNull();
        record.Properties.Should().Contain("EventName", "HttpRequestFailed");
        record.Properties.Should().Contain("Method", "GET");
        record.Properties.Should().Contain("Path", "/ed-fi/students");
        record.Properties.Should().Contain("StatusCode", 503);
        record.Properties.Should().Contain("TraceId", "core-trace-id");
        record.Properties.Should().ContainKey("DurationMs");
        _logger.Records.Should().NotContain(log => log.EventId.Name == "HttpRequestCompleted");
    }

    [Test]
    public async Task It_logs_core_pipeline_exceptions_as_request_failures_in_the_live_order()
    {
        var exception = new InvalidOperationException("boom");
        var requestInfo = No.RequestInfo("core-trace-id");
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Path = "/ed-fi/students" };
        var requestLogging = new RequestResponseLoggingMiddleware(_logger);
        var coreExceptionLogging = new CoreExceptionLoggingMiddleware(_logger);

        await requestLogging.Execute(
            requestInfo,
            () => coreExceptionLogging.Execute(requestInfo, () => throw exception)
        );

        var record = _logger.Records.Single(log => log.EventId.Name == "HttpRequestFailed");
        record.Level.Should().Be(LogLevel.Error);
        record.Exception.Should().BeSameAs(exception);
        record.Properties.Should().Contain("EventName", "HttpRequestFailed");
        record.Properties.Should().Contain("Method", "GET");
        record.Properties.Should().Contain("Path", "/ed-fi/students");
        record.Properties.Should().Contain("StatusCode", 500);
        record.Properties.Should().Contain("TraceId", "core-trace-id");
        record.Properties.Should().ContainKey("DurationMs");
        record.Properties["DurationMs"].Should().BeOfType<long>();
        _logger.Records.Should().NotContain(log => log.EventId.Name == "HttpRequestCompleted");
    }

    [Test]
    public async Task It_logs_failure_when_wrapped_by_core_exception_middleware()
    {
        var exception = new InvalidOperationException("boom");
        var requestInfo = No.RequestInfo("trace\r\nid\twith{unsafe}");
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Path = "/ed-fi/student\r\ns/{id}" };
        var requestLogging = new RequestResponseLoggingMiddleware(_logger);
        var coreExceptionLogging = new CoreExceptionLoggingMiddleware(_logger);

        await coreExceptionLogging.Execute(
            requestInfo,
            () => requestLogging.Execute(requestInfo, () => throw exception)
        );

        var record = _logger.Records.Single(log => log.EventId.Name == "HttpRequestFailed");
        record.Level.Should().Be(LogLevel.Error);
        record.Exception.Should().BeSameAs(exception);
        record.Properties.Should().Contain("EventName", "HttpRequestFailed");
        record.Properties.Should().Contain("TraceId", "traceidwithunsafe");
        record.Properties.Should().Contain("Method", "GET");
        record.Properties.Should().Contain("Path", "/ed-fi/students/id");
        record.Properties.Should().Contain("StatusCode", 500);
        record.Properties.Should().ContainKey("DurationMs");
        record.Properties["DurationMs"].Should().BeOfType<long>();
        record.Properties.Should().NotContainKey("Duration");
        record.ActiveScopes.Should().ContainSingle();
        var scope = record.ActiveScopes.Single();
        scope.Should().Contain("Application", "EdFi.DataManagementService");
        scope.Should().Contain("RequestLayer", "Core");
        scope.Should().Contain("TraceId", "traceidwithunsafe");
        scope.Should().Contain("Method", "GET");
        scope.Should().Contain("Path", "/ed-fi/students/id");
        scope.Should().NotContainKey("PathBase");
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
        var logger = loggerFactory.CreateLogger("RequestLogJsonContractTest");
        using var scope = logger.BeginScope(
            new Dictionary<string, object>
            {
                ["Application"] = "EdFi.DataManagementService",
                ["TraceId"] = "json-trace-id",
                ["RequestLayer"] = "Core",
                ["Method"] = "GET",
                ["Path"] = "/ed-fi/students",
                ["PathBase"] = "",
            }
        );

        logger.Log(
            LogLevel.Information,
            RequestLoggingEventIds.HttpRequestCompleted,
            "{EventName}: DMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
            "HttpRequestCompleted",
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

        (properties?["Application"]?.GetValue<string>()).Should().Be("EdFi.DataManagementService");
        (properties?["EventName"]?.GetValue<string>()).Should().Be("HttpRequestCompleted");
        (properties?["TraceId"]?.GetValue<string>()).Should().Be("json-trace-id");
        (properties?["RequestLayer"]?.GetValue<string>()).Should().Be("Core");
        (properties?["Method"]?.GetValue<string>()).Should().Be("GET");
        (properties?["Path"]?.GetValue<string>()).Should().Be("/ed-fi/students");
        (properties?["StatusCode"]?.GetValue<int>()).Should().Be(200);
        (properties?["DurationMs"]?.GetValue<long>()).Should().Be(42L);
        (json?["RenderedMessage"]?.GetValue<string>()).Should().Contain("DMS request completed");
        // JsonFormatter omits the Exception field when no exception is attached; the
        // docs/LOGGING.md collector contract documents the field as absent, not null.
        (json?.AsObject().ContainsKey("Exception"))
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task It_serializes_the_request_logging_middleware_source_context_in_serilog_json_output()
    {
        var sink = new CapturingSerilogSink();
        using var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger);
        var logger = loggerFactory.CreateLogger<RequestResponseLoggingMiddleware>();
        var middleware = new RequestResponseLoggingMiddleware(logger);
        var requestInfo = No.RequestInfo("json-trace-id");
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Path = "/ed-fi/students" };
        requestInfo.FrontendResponse = new FrontendResponse(200, Body: null, Headers: []);

        await middleware.Execute(requestInfo, TestHelper.NullNext);

        using var writer = new StringWriter();
        new JsonFormatter(renderMessage: true).Format(sink.Events.Single(), writer);
        var json = JsonNode.Parse(writer.ToString());
        var properties = json?["Properties"];

        (properties?["SourceContext"]?.GetValue<string>())
            .Should()
            .Be("EdFi.DataManagementService.Core.Middleware.RequestResponseLoggingMiddleware");
        (properties?["RequestLayer"]?.GetValue<string>()).Should().Be("Core");
        (properties?["EventName"]?.GetValue<string>()).Should().Be("HttpRequestCompleted");
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
