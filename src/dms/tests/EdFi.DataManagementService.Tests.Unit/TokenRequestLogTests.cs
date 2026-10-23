// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.StepDefinitions;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_Two_Token_Requests_In_The_Same_Log_Second
{
    private const string FirstRequestLog = """
        {"Timestamp":"2026-10-21T12:00:00.100Z","Properties":{"EventName":"HttpRequestCompleted","TraceId":"request-a","Method":"POST","StatusCode":401},"RenderedMessage":"request-b"}
        """;

    private const string SecondRequestLog = """
        {"Timestamp":"2026-10-21T12:00:00.200Z","Properties":{"EventName":"HttpRequestCompleted","TraceId":"request-b","Method":"POST","StatusCode":401}}
        """;

    [Test]
    public void It_does_not_accept_the_earlier_requests_event_for_the_later_request() =>
        StepDefinitions.TokenRequestLogContains(FirstRequestLog, "request-b", 401).Should().BeFalse();

    [Test]
    public void It_accepts_the_later_requests_own_structured_event() =>
        StepDefinitions
            .TokenRequestLogContains(FirstRequestLog + "\n" + SecondRequestLog, "request-b", 401)
            .Should()
            .BeTrue();

    [TestCase("request", 401)]
    [TestCase("request-b", 400)]
    public void It_requires_the_exact_trace_and_status(string traceId, int status) =>
        StepDefinitions.TokenRequestLogContains(SecondRequestLog, traceId, status).Should().BeFalse();

    [TestCase(
        """{"Properties":{"TraceId":"request-b","EventName":"OtherEvent","Method":"POST","StatusCode":401}}"""
    )]
    [TestCase(
        """{"Properties":{"TraceId":"request-b","EventName":"HttpRequestCompleted","Method":"GET","StatusCode":401}}"""
    )]
    [TestCase("request-b")]
    [TestCase("null")]
    public void It_rejects_non_request_events_and_unstructured_text(string log) =>
        StepDefinitions.TokenRequestLogContains(log, "request-b", 401).Should().BeFalse();

    [Test]
    public void It_accepts_a_structured_gateway_failure_among_other_diagnostics() =>
        StepDefinitions
            .TokenRequestLogContains(
                "not JSON\n{\"Properties\":{\"TraceId\":\"request-b\",\"EventName\":\"HttpRequestFailed\",\"Method\":\"POST\",\"StatusCode\":502}}",
                "request-b",
                502
            )
            .Should()
            .BeTrue();

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_reads_the_requests_event_after_a_record_split_across_chunks(bool followEndOfFile)
    {
        // The first read ends inside the request event. Losing the incomplete line would lose
        // correlation evidence when either the file sink or the Docker stream splits a write.
        using StringReader reader = new(new string(' ', 4090) + SecondRequestLog + "\n");
        // Only bounds a broken reader; the assertion has no dependency on elapsed time.
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(1));

        bool found = await StepDefinitions.TokenRequestLogContainsAsync(
            reader,
            "request-b",
            401,
            followEndOfFile,
            deadline.Token
        );

        found.Should().BeTrue();
    }

    [Test]
    public async Task It_rejects_a_stream_without_the_requests_own_event()
    {
        using StringReader reader = new(FirstRequestLog);

        bool found = await StepDefinitions.TokenRequestLogContainsAsync(
            reader,
            "request-b",
            401,
            false,
            CancellationToken.None
        );

        found.Should().BeFalse();
    }
}
