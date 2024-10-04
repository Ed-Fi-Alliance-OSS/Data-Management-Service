// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
public class RequestDataBodyLoggingMiddlewareTests
{
    private PipelineContext _context = No.PipelineContext();
    private ILogger<RequestDataBodyLoggingMiddleware>? _logger;
    private string _capturedLogMessage = string.Empty;
    private const string LogFilePath = "logs/test_logs.txt";

    internal static IPipelineStep Middleware(
        ILogger<RequestDataBodyLoggingMiddleware> logger,
        bool maskRequestBody
    )
    {
        return new RequestDataBodyLoggingMiddleware(logger, maskRequestBody);
    }

    [TestFixture]
    public class Given_A_LogLevel_Debug_And_MaskRequestBody_True : RequestDataBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            string? logDirectory = Path.GetDirectoryName(LogFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(LogFilePath))
            {
                File.Delete(LogFilePath);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(LogFilePath)
                .CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestDataBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/schools",
                    Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );

            _context = new(frontEndRequest, RequestMethod.POST);

            await Middleware(_logger!, true).Execute(_context, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync("logs/test_logs.txt");
        }

        [Test]
        public void It_has_a_body_with_hidden_values()
        {
            _capturedLogMessage.Should().Contain("{\"schoolId\":\"*\",\"nameOfInstitution\":\"*\"}");
        }
    }

    [TestFixture]
    public class Given_A_LogLevel_Debug_And_MaskRequestBody_False : RequestDataBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            string? logDirectory = Path.GetDirectoryName(LogFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(LogFilePath))
            {
                File.Delete(LogFilePath);
            }

            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.File(LogFilePath).CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestDataBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/schools",
                    Body: """{"schoolId":"12345","nameOfInstitution":"School Test"}""",
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );

            _context = new(frontEndRequest, RequestMethod.POST);

            await Middleware(_logger!, false).Execute(_context, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync("logs/test_logs.txt");
        }

        [Test]
        public void It_has_a_body_with_valid_values()
        {
            _capturedLogMessage
                .Should()
                .Contain("{\"schoolId\":\"12345\",\"nameOfInstitution\":\"School Test\"}");
        }
    }

    [TestFixture]
    public class Given_A_LogLevel_Verbose_And_MaskRequestBody_True : RequestDataBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            string? logDirectory = Path.GetDirectoryName(LogFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(LogFilePath))
            {
                File.Delete(LogFilePath);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(LogFilePath)
                .CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestDataBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/schools",
                    Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );

            _context = new(frontEndRequest, RequestMethod.POST);

            await Middleware(_logger!, true).Execute(_context, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync("logs/test_logs.txt");
        }

        [Test]
        public void It_has_a_body_with_hidden_values()
        {
            _capturedLogMessage.Should().Contain("{\"schoolId\":\"*\",\"nameOfInstitution\":\"*\"}");
        }
    }

    [TestFixture]
    public class Given_A_LogLevel_Information_And_MaskRequestBody_True : RequestDataBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            string? logDirectory = Path.GetDirectoryName(LogFilePath);
            if (logDirectory != null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (File.Exists(LogFilePath))
            {
                File.Delete(LogFilePath);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(LogFilePath)
                .CreateLogger();

            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestDataBodyLoggingMiddleware>();

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/schools",
                    Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );

            _context = new(frontEndRequest, RequestMethod.POST);

            await Middleware(_logger!, true).Execute(_context, NullNext);

            await Log.CloseAndFlushAsync();

            _capturedLogMessage = await File.ReadAllTextAsync("logs/test_logs.txt");
        }

        [Test]
        public void It_has_an_empty_log()
        {
            _capturedLogMessage.Should().BeEmpty();
        }
    }
}
