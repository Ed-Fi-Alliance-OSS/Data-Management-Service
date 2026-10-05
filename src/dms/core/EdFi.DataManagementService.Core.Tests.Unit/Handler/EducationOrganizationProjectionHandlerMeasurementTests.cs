// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Tests.Unit.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SetResult = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

/// <summary>
/// The step 2.6 handler measurement: managed allocations, stage times and serialized size per page,
/// and the handler's share of a full walk, at the cap and the default page size. The reader is a stub
/// that returns an already materialized set, so nothing here includes provider cost. Opt-in; it
/// prints <c>MEASURE</c> lines rather than asserting budgets.
/// </summary>
/// <remarks>
/// The set has the shape of the step 2.5 provider measurement: 1 state agency, 10 service centers,
/// 989 local agencies (every tenth below the first ten has a parent agency) and 49,000 schools,
/// with names of about 30 characters. Serialization uses the frontend's options so the byte count is
/// what the response would carry.
/// </remarks>
[TestFixture]
[Explicit("Measurement; run on demand")]
[Category("ProjectionMeasurement")]
public class Given_A_Full_Walk_Of_The_Handler_At_The_Cap
{
    private const int Walks = 5;
    private const int DataStoreId = 1;

    private static readonly JsonSerializerOptions _frontendSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static EducationOrganizationProjectionRow[] Set()
    {
        List<EducationOrganizationProjectionRow> rows =
        [
            ProjectionRows.Sea(1, "State Department of Education"),
        ];

        for (long id = 11; id <= 20; id++)
        {
            rows.Add(ProjectionRows.Esc(id, sea: 1, name: $"Regional Service Center {id:D6}"));
        }

        for (long id = 200_001; id <= 200_989; id++)
        {
            long? parent = id > 200_010 && id % 10 == 0 ? 200_001 + (id % 10) : null;
            rows.Add(
                ProjectionRows.Lea(
                    id,
                    parent: parent,
                    esc: 11 + (id % 10),
                    sea: 1,
                    name: $"Local Education Agency {id:D7}"
                )
            );
        }

        for (long id = 1_000_000; id < 1_049_000; id++)
        {
            rows.Add(
                ProjectionRows.School(id, lea: 200_001 + (id % 989), name: $"Independent School {id:D9}")
            );
        }

        return [.. rows];
    }

    [Test]
    public async Task It_reports_the_handler_cost()
    {
        EducationOrganizationProjectionRow[] rows = Set();
        rows.Should().HaveCount(50_000);

        var settings = new EducationOrganizationProjectionSettings();
        var reader = new StubProjectionSetReader { Answer = _ => new SetResult.Set(rows) };
        var observer = new StageTimer();
        var pipeline = new PipelineProvider([
            new ParseEducationOrganizationProjectionRequestMiddleware(
                settings,
                TimeProvider.System,
                NullLogger<ParseEducationOrganizationProjectionRequestMiddleware>.Instance
            ),
            new EducationOrganizationProjectionHandler(
                settings,
                TimeProvider.System,
                observer,
                NullLogger<EducationOrganizationProjectionHandler>.Instance
            ),
        ]);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IEducationOrganizationProjectionSetReader>(reader)
            .BuildServiceProvider();

        List<PageCost> pages = [];
        List<double> walkMilliseconds = [];

        // The first walk warms up JIT and is not reported.
        for (int walk = 0; walk <= Walks; walk++)
        {
            var walkTimer = Stopwatch.StartNew();
            string? cursor = null;

            do
            {
                Dictionary<string, string> query = new()
                {
                    ["dataStoreId"] = DataStoreId.ToString(),
                    ["limit"] = settings.MaximumPageSize.ToString(),
                };
                if (cursor is not null)
                {
                    query["cursor"] = cursor;
                }

                var requestInfo = new RequestInfo(
                    new FrontendRequest(
                        Path: "/management/education-organizations",
                        Body: null,
                        Form: null,
                        Headers: [],
                        QueryParameters: query,
                        TraceId: new TraceId("projection-measure"),
                        RouteQualifiers: []
                    ),
                    RequestMethod.GET,
                    services
                )
                {
                    MappingSet = EducationOrganizationProjectionHandlerTests.MappingSet,
                };

                observer.Reset();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var pageTimer = Stopwatch.StartNew();
                Task run = pipeline.Run(requestInfo);
                run.IsCompleted.Should()
                    .BeTrue("the stub reader completes synchronously, so the page runs on this thread");
                await run;
                double pageMilliseconds = pageTimer.Elapsed.TotalMilliseconds;
                observer.Complete();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                requestInfo.FrontendResponse.StatusCode.Should().Be(200);

                var serializeTimer = Stopwatch.StartNew();
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                    requestInfo.FrontendResponse.Body,
                    _frontendSerializerOptions
                );
                double serializeMilliseconds = serializeTimer.Elapsed.TotalMilliseconds;

                if (walk > 0)
                {
                    pages.Add(
                        new PageCost(
                            pageMilliseconds,
                            allocated,
                            observer.Milliseconds(ProjectionProcessingStage.Validation),
                            observer.Milliseconds(ProjectionProcessingStage.Precedence),
                            observer.Milliseconds(ProjectionProcessingStage.Hashing),
                            observer.Milliseconds(ProjectionProcessingStage.Slicing),
                            observer.Milliseconds(ProjectionProcessingStage.Publishing),
                            serializeMilliseconds,
                            body.Length
                        )
                    );
                }

                cursor = requestInfo.FrontendResponse.Body!["nextCursor"]?.GetValue<string>();
            } while (cursor is not null);

            if (walk > 0)
            {
                walkMilliseconds.Add(walkTimer.Elapsed.TotalMilliseconds);
            }
        }

        pages.Should().HaveCount(Walks * 25);

        var output = TestContext.Out;
        await output.WriteLineAsync(
            $"MEASURE handler rows={rows.Length} limit={settings.MaximumPageSize} pages_per_walk=25 walks={Walks}"
        );
        await output.WriteLineAsync(
            $"MEASURE handler page_ms {Summary([.. pages.Select(page => page.Milliseconds)])} "
                + $"allocated_mb_per_page {Summary([.. pages.Select(page => page.AllocatedBytes / 1_048_576.0)])}"
        );
        await output.WriteLineAsync(
            $"MEASURE handler stage_ms validation {Summary([.. pages.Select(page => page.Validation)])} "
                + $"precedence {Summary([.. pages.Select(page => page.Precedence)])} "
                + $"hashing {Summary([.. pages.Select(page => page.Hashing)])} "
                + $"slicing {Summary([.. pages.Select(page => page.Slicing)])} "
                + $"publishing {Summary([.. pages.Select(page => page.Publishing)])}"
        );
        await output.WriteLineAsync(
            $"MEASURE handler serialize_ms {Summary([.. pages.Select(page => page.SerializeMilliseconds)])} "
                + $"body_kb {Summary([.. pages.Select(page => page.BodyBytes / 1024.0)])}"
        );
        await output.WriteLineAsync($"MEASURE handler walk_ms {Summary(walkMilliseconds)}");
    }

    private sealed record PageCost(
        double Milliseconds,
        long AllocatedBytes,
        double Validation,
        double Precedence,
        double Hashing,
        double Slicing,
        double Publishing,
        double SerializeMilliseconds,
        int BodyBytes
    );

    private static string Summary(IReadOnlyCollection<double> values)
    {
        var sorted = values.Order().ToList();
        return $"min={sorted[0]:F2} p50={Percentile(sorted, 0.5):F2} p95={Percentile(sorted, 0.95):F2} max={sorted[^1]:F2}";
    }

    private static double Percentile(List<double> sorted, double percentile) =>
        sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(percentile * sorted.Count) - 1)];

    /// <summary>
    /// Times each stage from its entry to the next stage's entry; the last stage ends at
    /// <see cref="Complete"/>.
    /// </summary>
    private sealed class StageTimer : IProjectionProcessingObserver
    {
        private readonly Stopwatch _clock = new();
        private readonly Dictionary<ProjectionProcessingStage, (TimeSpan Start, TimeSpan? End)> _stages = [];
        private ProjectionProcessingStage? _current;

        public void Reset()
        {
            _stages.Clear();
            _current = null;
            _clock.Restart();
        }

        public void Stage(ProjectionProcessingStage stage)
        {
            if (_current is ProjectionProcessingStage previous)
            {
                _stages[previous] = (_stages[previous].Start, _clock.Elapsed);
            }

            _stages[stage] = (_clock.Elapsed, null);
            _current = stage;
        }

        public void Checkpoint(ProjectionProcessingStage stage, int rowsProcessed) { }

        /// <summary>Ends the stage in progress at the end of the page.</summary>
        public void Complete()
        {
            if (_current is ProjectionProcessingStage last)
            {
                _stages[last] = (_stages[last].Start, _clock.Elapsed);
            }
        }

        public double Milliseconds(ProjectionProcessingStage stage) =>
            _stages.TryGetValue(stage, out var span)
                ? ((span.End ?? span.Start) - span.Start).TotalMilliseconds
                : 0;
    }
}
