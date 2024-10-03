// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    [OneTimeSetUp]
    public void OneTimeSetup()
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
    }

    internal static IPipelineStep Middleware(
        ILogger<RequestDataBodyLoggingMiddleware> logger,
        IOptions<RequestLoggingOptions> options
    )
    {
        return new RequestDataBodyLoggingMiddleware(logger, options);
    }

    [TestFixture]
    public class Given_A_LogLevel_Of_Debug_And_MaskRequestBody_True : RequestDataBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestDataBodyLoggingMiddleware>();

            var loggingOptions = new OptionsWrapper<RequestLoggingOptions>(
                new RequestLoggingOptions { LogLevel = "Debug", MaskRequestBody = true }
            );

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/schools",
                    Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );

            _context = new(frontEndRequest, RequestMethod.POST);

            await Middleware(_logger!, loggingOptions).Execute(_context, NullNext);

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
    public class Given_A_LogLevel_Of_Debug_And_MaskRequestBody_False : RequestDataBodyLoggingMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<RequestDataBodyLoggingMiddleware>();

            var loggingOptions = new OptionsWrapper<RequestLoggingOptions>(
                new RequestLoggingOptions { LogLevel = "Debug", MaskRequestBody = false }
            );

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/schools",
                    Body: """{"schoolId":"12345","nameOfInstitution":"School Test"}""",
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );

            _context = new(frontEndRequest, RequestMethod.POST);

            await Middleware(_logger!, loggingOptions).Execute(_context, NullNext);

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
}
