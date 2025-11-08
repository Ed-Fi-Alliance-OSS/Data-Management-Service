// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Extensions.Logging;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[NonParallelizable]
public class RequestInfoBodyLoggingMiddlewareTests
{
    private RequestInfo _requestInfo = No.RequestInfo();
    private ILogger<RequestInfoBodyLoggingMiddleware>? _logger;
    private string _capturedLogMessage = string.Empty;
    private string _logFilePath = string.Empty;

    internal static IPipelineStep Middleware(
        ILogger<RequestInfoBodyLoggingMiddleware> logger,
        bool maskRequestBody
    )
    {
        return new RequestInfoBodyLoggingMiddleware(logger, maskRequestBody);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_A_LogLevel_Debug_And_MaskRequestBody_True : RequestInfoBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _logFilePath = Path.Combine("logs", $"test_logs_{Guid.NewGuid()}.txt");
            string? logDirectory = Path.GetDirectoryName(_logFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(_logFilePath)
                .CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestInfoBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );

            _requestInfo = new(frontEndRequest, RequestMethod.POST)
            {
                ParsedBody = System.Text.Json.Nodes.JsonNode.Parse(frontEndRequest.Body!)!,
            };

            await Middleware(_logger!, true).Execute(_requestInfo, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync(_logFilePath);
        }

        [Test]
        public void It_has_a_body_with_hidden_values()
        {
            _capturedLogMessage.Should().Contain("{\"schoolId\":\"*\",\"nameOfInstitution\":\"*\"}");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_A_LogLevel_Debug_And_MaskRequestBody_False : RequestInfoBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _logFilePath = Path.Combine("logs", $"test_logs_{Guid.NewGuid()}.txt");
            string? logDirectory = Path.GetDirectoryName(_logFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(_logFilePath)
                .CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestInfoBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{"schoolId":"12345","nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );

            _requestInfo = new(frontEndRequest, RequestMethod.POST)
            {
                ParsedBody = System.Text.Json.Nodes.JsonNode.Parse(frontEndRequest.Body!)!,
            };

            await Middleware(_logger!, false).Execute(_requestInfo, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync(_logFilePath);
        }

        [Test]
        public void It_has_a_body_with_valid_values()
        {
            _capturedLogMessage
                .Should()
                .Contain("{\"schoolId\":\"12345\",\"nameOfInstitution\":\"School Test\"}");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_A_LogLevel_Verbose_And_MaskRequestBody_True : RequestInfoBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _logFilePath = Path.Combine("logs", $"test_logs_{Guid.NewGuid()}.txt");
            string? logDirectory = Path.GetDirectoryName(_logFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(_logFilePath)
                .CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestInfoBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );

            _requestInfo = new(frontEndRequest, RequestMethod.POST)
            {
                ParsedBody = System.Text.Json.Nodes.JsonNode.Parse(frontEndRequest.Body!)!,
            };

            await Middleware(_logger!, true).Execute(_requestInfo, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync(_logFilePath);
        }

        [Test]
        public void It_has_a_body_with_hidden_values()
        {
            _capturedLogMessage.Should().Contain("{\"schoolId\":\"*\",\"nameOfInstitution\":\"*\"}");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_A_LogLevel_Information_And_MaskRequestBody_True : RequestInfoBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _logFilePath = Path.Combine("logs", $"test_logs_{Guid.NewGuid()}.txt");
            string? logDirectory = Path.GetDirectoryName(_logFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(_logFilePath)
                .CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestInfoBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );

            _requestInfo = new(frontEndRequest, RequestMethod.POST)
            {
                ParsedBody = JsonNode.Parse(frontEndRequest.Body!)!,
            };

            await Middleware(_logger!, true).Execute(_requestInfo, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync(_logFilePath);
        }

        [Test]
        public void It_has_an_empty_log()
        {
            _capturedLogMessage.Should().BeEmpty();
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }
        }
    }
}
