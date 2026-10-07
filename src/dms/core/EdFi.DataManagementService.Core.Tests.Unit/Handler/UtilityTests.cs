// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

/// <summary>
/// DMS-1576: If-None-Match is a GET-only conditional-read validator; POST, PUT, and DELETE ignore it.
/// These tests exercise the shared Debug-log helper directly. The write handler fixtures only prove the
/// helper is wired in.
/// </summary>
[TestFixture]
[Parallelizable]
public class UtilityTests
{
    private const string SentinelTag = "\"sentinel-7f3a\"";
    private const string RequestTraceId = "if-none-match-trace";

    private static RequestInfo RequestInfoWithHeaders(Dictionary<string, string> headers)
    {
        var requestInfo = No.RequestInfo(RequestTraceId);
        requestInfo.Method = RequestMethod.POST;
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Headers = headers };
        return requestInfo;
    }

    private static Dictionary<string, string> HeadersWithIfNoneMatch(string value) =>
        new(StringComparer.OrdinalIgnoreCase) { ["If-None-Match"] = value };

    private static IReadOnlyList<LogRecord> IgnoredRecords(RecordingLogger logger) =>
        logger
            .Records.Where(record =>
                record.Level == LogLevel.Debug && record.Message.Contains("If-None-Match")
            )
            .ToList();

    [TestFixture]
    [Parallelizable]
    public class Given_A_Write_Request_That_Carries_An_If_None_Match_Header
    {
        private RecordingLogger _logger = new();

        [SetUp]
        public void Setup()
        {
            _logger = new RecordingLogger();

            Utility.LogIfNoneMatchIgnoredOnWrite(
                _logger,
                RequestInfoWithHeaders(HeadersWithIfNoneMatch(SentinelTag))
            );
        }

        [Test]
        public void It_logs_once_at_debug_that_if_none_match_was_ignored()
        {
            IgnoredRecords(_logger).Should().ContainSingle();
        }

        [Test]
        public void It_logs_the_method_and_trace_id_as_structured_properties()
        {
            var record = IgnoredRecords(_logger).Single();

            record.Properties["Method"].Should().Be("POST");
            record.Properties["TraceId"].Should().Be(RequestTraceId);
        }

        [Test]
        public void It_does_not_log_the_header_value_in_the_message_or_any_property()
        {
            var record = IgnoredRecords(_logger).Single();

            record.Message.Should().NotContain("sentinel-7f3a");
            record
                .Properties.Values.Select(value => value?.ToString())
                .Should()
                .NotContain(value => value != null && value.Contains("sentinel-7f3a"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Write_Request_That_Carries_A_Blank_If_None_Match_Header
    {
        private RecordingLogger _logger = new();

        [SetUp]
        public void Setup()
        {
            _logger = new RecordingLogger();

            // The frontend passes a blank value through to core. The helper only checks for the header's
            // presence, so a blank value is still logged once as ignored.
            Utility.LogIfNoneMatchIgnoredOnWrite(_logger, RequestInfoWithHeaders(HeadersWithIfNoneMatch("")));
        }

        [Test]
        public void It_logs_once_at_debug_that_the_blank_if_none_match_was_ignored()
        {
            IgnoredRecords(_logger).Should().ContainSingle();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Write_Request_Without_An_If_None_Match_Header
    {
        private RecordingLogger _logger = new();

        [SetUp]
        public void Setup()
        {
            _logger = new RecordingLogger();

            Utility.LogIfNoneMatchIgnoredOnWrite(
                _logger,
                RequestInfoWithHeaders(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            );
        }

        [Test]
        public void It_does_not_log_that_if_none_match_was_ignored()
        {
            _logger.Records.Should().NotContain(record => record.Message.Contains("If-None-Match"));
        }
    }
}
