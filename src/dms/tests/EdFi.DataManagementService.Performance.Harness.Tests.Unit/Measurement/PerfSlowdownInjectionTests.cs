// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_An_Injection_Spec
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void It_means_no_injection_when_unset(string? value)
    {
        PerfSlowdownSpec.Parse(value).IsNone.Should().BeTrue();
    }

    [Test]
    public void It_parses_every_entry()
    {
        PerfSlowdownSpec
            .Parse("student-put-collection-update=0.4; traditional-offset-zero/25=1.5;")
            .Factors.Should()
            .BeEquivalentTo(
                new Dictionary<string, decimal>
                {
                    ["student-put-collection-update"] = 0.4m,
                    ["traditional-offset-zero/25"] = 1.5m,
                }
            );
    }

    [Test]
    public void It_accepts_the_maximum_factor()
    {
        PerfSlowdownSpec.Parse("student-put-collection-update=5").Factors.Values.Should().Equal(5m);
    }

    [TestCase("student-put-collection-update=0")]
    [TestCase("student-put-collection-update=-1")]
    [TestCase("student-put-collection-update=5.01")]
    [TestCase("student-put-collection-update=fast")]
    public void It_rejects_a_factor_out_of_bounds(string value)
    {
        Action parse = () => PerfSlowdownSpec.Parse(value);

        parse.Should().Throw<PerfConfigurationException>().WithMessage("*factor must be a number in (0, 5]*");
    }

    [TestCase("traditional-offset-zero=1", "unknown scenario 'traditional-offset-zero'")]
    [TestCase("student-put=1", "unknown scenario 'student-put'")]
    [TestCase("student-put-collection-update", "must be <scenarioId>=<factor>")]
    [TestCase("student-put-collection-update=1;student-put-collection-update=2", "listed twice")]
    public void It_rejects_a_malformed_entry(string value, string expected)
    {
        Action parse = () => PerfSlowdownSpec.Parse(value);

        parse.Should().Throw<PerfConfigurationException>().WithMessage($"*{expected}*");
    }
}

/// <summary>
/// A timestamp source that advances one tick per read; the fake pipeline advances it by
/// <see cref="PipelineTicks" />. No wall time is involved.
/// </summary>
internal sealed class FakeTimestamp
{
    public const long PipelineTicks = 1_000;

    public long Now { get; set; }

    public long Read() => Now++;

    public RequestDelegate Pipeline(string body, Action<HttpContext>? whileRunning = null) =>
        async context =>
        {
            Now += PipelineTicks;
            await context.Response.WriteAsync(body);
            whileRunning?.Invoke(context);
        };
}

[TestFixture]
public class Given_The_Injection_Middleware
{
    private const long DeepOffset = 9_000;
    private static readonly string _studentPath =
        $"{PerfFixtureDefinition.ResourceEndpoint}/{PerfFixtureDefinition.DocumentUuidFor(7)}";

    private FakeTimestamp _clock = null!;
    private PerfSlowdownInjection _injection = null!;

    [SetUp]
    public void Setup()
    {
        _clock = new FakeTimestamp();
        _injection = new PerfSlowdownInjection(
            PerfSlowdownSpec.Parse("student-put-collection-update=0.5;traditional-offset-zero/25=2"),
            DeepOffset,
            _clock.Read
        );
    }

    private static DefaultHttpContext Request(string method, string path, string query = "")
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseText(HttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    [Test]
    public async Task It_waits_factor_times_the_pipeline_time_for_a_matching_write()
    {
        DefaultHttpContext context = Request("PUT", _studentPath);

        await _injection.InvokeAsync(context, _clock.Pipeline(string.Empty));

        // 1,000 pipeline ticks, then about 0.5 x 1,000 ticks of busy-wait.
        _clock.Now.Should().BeInRange(1_500, 1_510);
    }

    [Test]
    public async Task It_waits_factor_times_the_pipeline_time_for_a_matching_read_cell()
    {
        DefaultHttpContext context = Request(
            "GET",
            PerfFixtureDefinition.ResourceEndpoint,
            "?limit=25&offset=0"
        );

        await _injection.InvokeAsync(context, _clock.Pipeline("[]"));

        _clock.Now.Should().BeInRange(3_000, 3_010);
    }

    [Test]
    public async Task It_holds_the_response_body_until_the_wait_ends()
    {
        DefaultHttpContext context = Request(
            "GET",
            PerfFixtureDefinition.ResourceEndpoint,
            "?limit=25&offset=0"
        );
        MemoryStream client = (MemoryStream)context.Response.Body;
        long writtenBeforeWait = -1;

        await _injection.InvokeAsync(
            context,
            _clock.Pipeline("[1,2,3]", _ => writtenBeforeWait = client.Length)
        );

        writtenBeforeWait.Should().Be(0);
        ResponseText(context).Should().Be("[1,2,3]");
    }

    [TestCase("GET", "/data/ed-fi/schools", "")]
    [TestCase("GET", "/data/ed-fi/students", "?limit=25&offset=7")]
    [TestCase("GET", "/data/ed-fi/students", "?limit=25&offset=0&totalCount=true")]
    [TestCase("PUT", "/data/ed-fi/students", "")]
    [TestCase("POST", "/data/ed-fi/students", "")]
    [TestCase("DELETE", "/data/ed-fi/students/8f7a0000-0000-4000-8000-000000000007", "")]
    public async Task It_passes_through_a_request_matching_no_scenario(
        string method,
        string path,
        string query
    )
    {
        DefaultHttpContext context = Request(method, path, query);

        await _injection.InvokeAsync(context, _clock.Pipeline("body"));

        _clock.Now.Should().Be(FakeTimestamp.PipelineTicks);
        ResponseText(context).Should().Be("body");
        _injection.AffectedRequests.Values.Should().AllSatisfy(count => count.Should().Be(0));
    }

    [Test]
    public async Task It_passes_through_a_recognized_scenario_that_is_not_injected()
    {
        DefaultHttpContext context = Request(
            "GET",
            PerfFixtureDefinition.ResourceEndpoint,
            "?limit=500&offset=9000"
        );

        _injection.ScenarioOf(context.Request).Should().Be("traditional-offset-deep/500");
        await _injection.InvokeAsync(context, _clock.Pipeline("[]"));

        _clock.Now.Should().Be(FakeTimestamp.PipelineTicks);
        _injection.AffectedRequests["traditional-offset-deep/500"].Should().Be(0);
    }

    [Test]
    public async Task It_counts_affected_requests_per_scenario()
    {
        await _injection.InvokeAsync(Request("PUT", _studentPath), _clock.Pipeline(string.Empty));
        await _injection.InvokeAsync(Request("PUT", _studentPath), _clock.Pipeline(string.Empty));
        await _injection.InvokeAsync(
            Request("GET", PerfFixtureDefinition.ResourceEndpoint, "?limit=25&offset=0"),
            _clock.Pipeline("[]")
        );
        await _injection.InvokeAsync(
            Request("GET", PerfFixtureDefinition.ResourceEndpoint, "?limit=500&offset=0"),
            _clock.Pipeline("[]")
        );

        _injection
            .AffectedRequests.Should()
            .BeEquivalentTo(
                PerfBenchmarkScenarios.AllIds.ToDictionary(
                    id => id,
                    id =>
                        id switch
                        {
                            "student-put-collection-update" => 2,
                            "traditional-offset-zero/25" => 1,
                            _ => 0,
                        }
                )
            );
    }

    [Test]
    public async Task It_runs_ahead_of_the_application_pipeline_as_a_startup_filter()
    {
        ApplicationBuilder app = new(new ServiceCollection().BuildServiceProvider());
        _injection.Configure(builder => builder.Run(_clock.Pipeline(string.Empty)))(app);

        await app.Build()(Request("PUT", _studentPath));

        _injection.AffectedRequests["student-put-collection-update"].Should().Be(1);
        _clock.Now.Should().BeInRange(1_500, 1_510);
    }
}
