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
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// The rendering of ignored query parameter names shared by the <c>X-EdFi-Warning</c> header and the
/// Debug log event, and how the warning reaches the response a request ends with.
/// </summary>
[TestFixture]
[Parallelizable]
public class IgnoredQueryParameterWarningTests
{
    private static string Render(params string[] names) => IgnoredQueryParameterWarning.RenderNames(names);

    [TestFixture]
    [Parallelizable]
    public class Given_Plain_Ascii_Names : IgnoredQueryParameterWarningTests
    {
        [Test]
        public void It_lists_them_unchanged_in_the_order_given()
        {
            Render("studentUniqueld", "foo", "a-b.c_d~e").Should().Be("studentUniqueld, foo, a-b.c_d~e");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Names_With_Hostile_Characters : IgnoredQueryParameterWarningTests
    {
        [Test]
        public void It_removes_line_breaks_rather_than_encoding_them()
        {
            Render("a\r\nX-Injected: 1").Should().Be("aX-Injected%3A%201");
        }

        [TestCase("a\tb", "ab", TestName = "horizontal tab")]
        [TestCase("a\u0000b", "ab", TestName = "null")]
        [TestCase("a\u0085b", "ab", TestName = "next line")]
        [TestCase("a\u2028b\u2029c", "abc", TestName = "line and paragraph separators")]
        [TestCase("a\u200Bb\u202Ec", "abc", TestName = "zero width space and bidi override")]
        public void It_removes_control_format_and_separator_characters(string name, string expected)
        {
            Render(name).Should().Be(expected);
        }

        [Test]
        public void It_removes_an_unpaired_surrogate_and_keeps_a_paired_one()
        {
            Render("a\uD800b😀").Should().Be("ab%F0%9F%98%80");
        }

        [Test]
        public void It_encodes_the_separator_and_marker_characters()
        {
            Render("a, (b)").Should().Be("a%2C%20%28b%29");
        }

        [Test]
        public void It_encodes_non_ascii_text_as_utf8()
        {
            Render("élève").Should().Be("%C3%A9l%C3%A8ve");
        }

        [Test]
        public void It_encodes_a_percent_sign_so_the_encoding_stays_reversible()
        {
            Render("100%").Should().Be("100%25");
        }

        [TestCase("", TestName = "empty")]
        [TestCase("\r\n\u200B", TestName = "nothing left after removal")]
        public void It_renders_a_name_with_nothing_left_as_the_empty_marker(string name)
        {
            Render(name).Should().Be("(empty)");
        }

        [Test]
        public void It_renders_only_printable_ascii()
        {
            string rendered = Render("a\r\nb", "é\u0001", "\uD800", new string('ÿ', 100), "x y");

            rendered.Should().MatchRegex("^[\\x20-\\x7E]*$");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Long_Name : IgnoredQueryParameterWarningTests
    {
        [Test]
        public void It_keeps_a_name_of_exactly_the_encoded_budget()
        {
            string name = new('a', 64);

            Render(name).Should().Be(name);
        }

        [Test]
        public void It_cuts_a_name_one_past_the_budget_and_marks_it()
        {
            Render(new string('a', 65)).Should().Be(new string('a', 64) + "(truncated)");
        }

        // 63 plain characters leave one, which cannot hold a three-character triplet.
        [Test]
        public void It_never_splits_a_percent_triplet()
        {
            Render(new string('a', 63) + " ").Should().Be(new string('a', 63) + "(truncated)");
        }

        // 'é' encodes as two triplets; 60 plain characters leave four, enough for one triplet only.
        [Test]
        public void It_never_splits_a_multi_byte_character()
        {
            Render(new string('a', 60) + "é").Should().Be(new string('a', 60) + "(truncated)");
        }

        [Test]
        public void It_does_not_mark_a_name_whose_cut_tail_would_have_been_removed()
        {
            Render(new string('a', 64) + "\r\n\u200B").Should().Be(new string('a', 64));
        }

        // Each 'ÿ' encodes as two triplets, so ten fit in 60 characters and the eleventh does not.
        [Test]
        public void It_bounds_a_very_long_name()
        {
            Render(new string('ÿ', 100_000))
                .Should()
                .Be(string.Concat(Enumerable.Repeat("%C3%BF", 10)) + "(truncated)");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_More_Names_Than_Are_Listed : IgnoredQueryParameterWarningTests
    {
        [Test]
        public void It_lists_the_first_ten_and_counts_the_rest()
        {
            string[] names = [.. Enumerable.Range(1, 13).Select(index => $"p{index}")];

            Render(names).Should().Be("p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, (and 3 more)");
        }

        [Test]
        public void It_has_no_overflow_marker_at_exactly_ten()
        {
            string[] names = [.. Enumerable.Range(1, 10).Select(index => $"p{index}")];

            Render(names).Should().NotContain("more");
        }

        [Test]
        public void It_stays_within_the_documented_bound()
        {
            string[] names = [.. Enumerable.Range(1, 5000).Select(_ => new string(',', 1000))];

            (IgnoredQueryParameterWarning.HeaderPrefix + Render(names))
                .Length.Should()
                .BeLessThanOrEqualTo(817);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_That_Ignored_Parameters : IgnoredQueryParameterWarningTests
    {
        private static RequestInfo NewRequestInfo() =>
            new(
                new FrontendRequest(
                    Path: "/ed-fi/academicWeeks",
                    Body: null,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("warning-trace-id"),
                    RouteQualifiers: []
                ),
                RequestMethod.GET,
                ServiceProviderWithEffectiveTarget()
            );

        [Test]
        public async Task It_adds_the_header_to_a_downstream_non_success_response_and_keeps_the_rest()
        {
            RequestInfo requestInfo = NewRequestInfo();
            JsonObject body = new() { ["status"] = 403 };

            await IgnoredQueryParameterWarning.ReportAround(
                requestInfo,
                ["studentUniqueld"],
                new CapturingLogger(),
                () =>
                {
                    requestInfo.FrontendResponse = new FrontendResponse(
                        StatusCode: 403,
                        Body: body,
                        Headers: new() { ["Existing"] = "kept" },
                        LocationHeaderPath: "/somewhere",
                        ContentType: "application/problem+json"
                    );
                    return Task.CompletedTask;
                }
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            requestInfo.FrontendResponse.Body.Should().BeSameAs(body);
            requestInfo.FrontendResponse.LocationHeaderPath.Should().Be("/somewhere");
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            requestInfo
                .FrontendResponse.Headers.Should()
                .BeEquivalentTo(
                    new Dictionary<string, string>
                    {
                        ["Existing"] = "kept",
                        ["X-EdFi-Warning"] = "Ignored query parameters: studentUniqueld",
                    }
                );
        }

        [Test]
        public async Task It_does_not_change_the_header_dictionary_of_the_downstream_response()
        {
            RequestInfo requestInfo = NewRequestInfo();
            Dictionary<string, string> downstreamHeaders = new() { ["Existing"] = "kept" };

            await IgnoredQueryParameterWarning.ReportAround(
                requestInfo,
                ["x"],
                new CapturingLogger(),
                () =>
                {
                    requestInfo.FrontendResponse = new FrontendResponse(200, null, downstreamHeaders);
                    return Task.CompletedTask;
                }
            );

            downstreamHeaders.Keys.Should().Equal("Existing");
        }

        [Test]
        public async Task It_leaves_a_pipeline_that_assigned_no_response_alone()
        {
            RequestInfo requestInfo = NewRequestInfo();

            await IgnoredQueryParameterWarning.ReportAround(
                requestInfo,
                ["x"],
                new CapturingLogger(),
                () => Task.CompletedTask
            );

            requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }

        [Test]
        public async Task It_logs_one_debug_event_with_the_rendered_names_count_and_trace_id()
        {
            CapturingLogger logger = new();

            await IgnoredQueryParameterWarning.ReportAround(
                NewRequestInfo(),
                ["bad\r\nname", "other"],
                logger,
                () => Task.CompletedTask
            );

            CapturingLogger.Entry entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Debug);
            entry.State["IgnoredQueryParameters"].Should().Be("badname, other");
            entry.State["IgnoredQueryParameterCount"].Should().Be(2);
            entry.State["TraceId"].Should().Be("warning-trace-id");
            entry.Message.Should().NotContain("\r").And.NotContain("\n");
        }

        [Test]
        public async Task It_logs_nothing_when_debug_is_disabled()
        {
            CapturingLogger logger = new() { DebugEnabled = false };

            await IgnoredQueryParameterWarning.ReportAround(
                NewRequestInfo(),
                ["x"],
                logger,
                () => Task.CompletedTask
            );

            logger.Entries.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_That_Ignored_Nothing : IgnoredQueryParameterWarningTests
    {
        [Test]
        public async Task It_neither_logs_nor_changes_the_response()
        {
            RequestInfo requestInfo = new(
                new FrontendRequest(
                    Path: "/ed-fi/academicWeeks",
                    Body: null,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("t"),
                    RouteQualifiers: []
                ),
                RequestMethod.GET,
                ServiceProviderWithEffectiveTarget()
            );
            FrontendResponse downstream = new(200, null, new() { ["Existing"] = "kept" });
            CapturingLogger logger = new();

            await IgnoredQueryParameterWarning.ReportAround(
                requestInfo,
                [],
                logger,
                () =>
                {
                    requestInfo.FrontendResponse = downstream;
                    return Task.CompletedTask;
                }
            );

            requestInfo.FrontendResponse.Should().BeSameAs(downstream);
            logger.Entries.Should().BeEmpty();
        }
    }

    /// <summary>
    /// Captures every event with its structured state, so a test can assert what a sink receives.
    /// </summary>
    internal sealed class CapturingLogger : ILogger
    {
        internal sealed record Entry(
            LogLevel Level,
            string Message,
            IReadOnlyDictionary<string, object?> State
        );

        public List<Entry> Entries { get; } = [];

        public bool DebugEnabled { get; init; } = true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug || DebugEnabled;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Dictionary<string, object?> values = [];
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (KeyValuePair<string, object?> pair in pairs)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            Entries.Add(new Entry(logLevel, formatter(state, exception), values));
        }
    }
}
