// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection.ProjectionPages;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using Kind = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.ProjectionReadCheckpointKind;
using Stage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

/// <summary>
/// Caller cancellation and the read deadline during processing that follows a completed HTTP request. A test acts at
/// one named checkpoint through the reader's checkpoint seam, so nothing depends on timing: the read has two pages of
/// 1,500 schools, so the second page's validation passes one checkpoint interval and the set passes two.
/// </summary>
public class EducationOrganizationProjectionReaderCheckpointTests
{
    private const int PageSize = 1500;

    private static ProjectionReaderHarness TwoFullPages(Func<HttpResponseMessage>? lastPage = null) =>
        ProjectionReaderHarness.Serving(
            [
                () => Page("c1", Schools([.. Enumerable.Range(1, PageSize).Select(id => (long)id)])),
                lastPage
                    ?? (
                        () =>
                            Page(
                                null,
                                Schools([.. Enumerable.Range(PageSize + 1, PageSize).Select(id => (long)id)])
                            )
                    ),
            ],
            settings => settings.PageSize = PageSize
        );

    [TestFixture]
    public class Given_a_read_that_completes
    {
        private readonly List<ProjectionReadCheckpoint> _checkpoints = [];

        [SetUp]
        public async Task Setup()
        {
            _checkpoints.Clear();
            ProjectionReaderHarness harness = TwoFullPages();
            harness.Reader.CheckpointReached = _checkpoints.Add;
            ShouldSucceed(await harness.ReadAsync());
        }

        private static void ShouldSucceed(EducationOrganizationProjectionReadResult result) =>
            result
                .Should()
                .BeOfType<EducationOrganizationProjectionReadResult.Success>()
                .Which.Items.Should()
                .HaveCount(2 * PageSize);

        [Test]
        public void It_checks_around_each_parse_every_interval_and_before_the_outcome() =>
            _checkpoints
                .Should()
                .Equal(
                    new ProjectionReadCheckpoint(Kind.BeforeParse, 1, 0),
                    new ProjectionReadCheckpoint(Kind.AfterParse, 1, 0),
                    new ProjectionReadCheckpoint(Kind.ItemValidation, 1, 1024),
                    new ProjectionReadCheckpoint(Kind.BeforeParse, 2, 0),
                    new ProjectionReadCheckpoint(Kind.AfterParse, 2, 0),
                    new ProjectionReadCheckpoint(Kind.ItemValidation, 2, 1024),
                    new ProjectionReadCheckpoint(Kind.ParentIndex, 0, 1024),
                    new ProjectionReadCheckpoint(Kind.ParentIndex, 0, 2048),
                    new ProjectionReadCheckpoint(Kind.ParentResolution, 0, 1024),
                    new ProjectionReadCheckpoint(Kind.ParentResolution, 0, 2048),
                    new ProjectionReadCheckpoint(Kind.BeforeOutcome, 0, 0)
                );
    }

    /// <summary>
    /// At one checkpoint of the last page or the final passes, the caller cancels, the clock reaches the read deadline,
    /// or both. Cancellation throws with the caller's token and wins over the deadline; the deadline alone is a
    /// Page-stage <c>Timeout</c>. Either way no items are returned, nothing is logged as a success, and the read stops at
    /// that checkpoint.
    /// </summary>
    [TestFixture("BeforeParse", 2, 0, "cancel")]
    [TestFixture("BeforeParse", 2, 0, "deadline")]
    [TestFixture("BeforeParse", 2, 0, "both")]
    [TestFixture("AfterParse", 2, 0, "cancel")]
    [TestFixture("AfterParse", 2, 0, "deadline")]
    [TestFixture("AfterParse", 2, 0, "both")]
    [TestFixture("ItemValidation", 2, 1024, "cancel")]
    [TestFixture("ItemValidation", 2, 1024, "deadline")]
    [TestFixture("ItemValidation", 2, 1024, "both")]
    [TestFixture("ParentIndex", 0, 1024, "cancel")]
    [TestFixture("ParentIndex", 0, 1024, "deadline")]
    [TestFixture("ParentIndex", 0, 1024, "both")]
    [TestFixture("ParentResolution", 0, 2048, "cancel")]
    [TestFixture("ParentResolution", 0, 2048, "deadline")]
    [TestFixture("ParentResolution", 0, 2048, "both")]
    [TestFixture("BeforeOutcome", 0, 0, "cancel")]
    [TestFixture("BeforeOutcome", 0, 0, "deadline")]
    [TestFixture("BeforeOutcome", 0, 0, "both")]
    public class Given_an_interruption_during_processing(string kind, int page, int index, string action)
    {
        private readonly ProjectionReadCheckpoint _target = new(Enum.Parse<Kind>(kind), page, index);
        private CancellationTokenSource _caller = null!;
        private ProjectionReaderHarness _harness = null!;
        private List<ProjectionReadCheckpoint> _checkpoints = null!;
        private EducationOrganizationProjectionReadResult? _result;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            _checkpoints = [];
            _result = null;
            _exception = null;
            _harness = TwoFullPages();
            _harness.Reader.CheckpointReached = checkpoint =>
            {
                _checkpoints.Add(checkpoint);
                if (checkpoint != _target)
                {
                    return;
                }
                if (action is "deadline" or "both")
                {
                    _harness.Time.SetUtcNow(
                        ProjectionReaderHarness.Start.AddSeconds(_harness.Settings.TotalReadTimeoutSeconds)
                    );
                }
                if (action is "cancel" or "both")
                {
                    _caller.Cancel();
                }
            };

            try
            {
                _result = await _harness.ReadAsync(_caller.Token);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_throws_with_the_caller_token_or_times_out()
        {
            if (action == "deadline")
            {
                EducationOrganizationProjectionFailure failure = _result
                    .Should()
                    .BeOfType<EducationOrganizationProjectionReadResult.Failure>()
                    .Subject.Detail;
                (failure.Code, failure.Stage, failure.HttpStatus, failure.PagesRead, failure.Restarts)
                    .Should()
                    .Be(
                        (
                            Code.Timeout,
                            Stage.Page,
                            null,
                            _target.Kind is Kind.BeforeParse or Kind.AfterParse ? 1 : 2,
                            0
                        )
                    );
            }
            else
            {
                _exception
                    .Should()
                    .BeAssignableTo<OperationCanceledException>()
                    .Which.CancellationToken.Should()
                    .Be(_caller.Token);
            }
        }

        /// <summary>Nothing more is processed; a timeout still passes the gate before the outcome.</summary>
        [Test]
        public void It_stops_processing_at_that_checkpoint() =>
            _checkpoints
                .SkipWhile(checkpoint => checkpoint != _target)
                .Should()
                .Equal(
                    action == "deadline" && _target.Kind != Kind.BeforeOutcome
                        ? [_target, new ProjectionReadCheckpoint(Kind.BeforeOutcome, 0, 0)]
                        : new[] { _target }
                );

        [Test]
        public void It_logs_no_success() =>
            _harness
                .Recorder.Records.Where(record =>
                    record.Category == typeof(EducationOrganizationProjectionReader).FullName
                )
                .Select(record => record.Level)
                .Should()
                .Equal(action == "deadline" ? new[] { LogLevel.Warning } : Array.Empty<LogLevel>());
    }

    /// <summary>
    /// A failure already found keeps its code when the deadline passes before it is returned, but caller cancellation
    /// still wins over it.
    /// </summary>
    [TestFixture("cancel")]
    [TestFixture("deadline")]
    public class Given_an_interruption_before_a_failure_is_returned(string action)
    {
        private CancellationTokenSource _caller = null!;
        private EducationOrganizationProjectionReadResult? _result;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            ProjectionReaderHarness harness = ProjectionReaderHarness.Serving([
                () => Page(null, Item(1, Lea, parentId: 1)),
            ]);
            harness.Reader.CheckpointReached = checkpoint =>
            {
                if (checkpoint.Kind != Kind.BeforeOutcome)
                {
                    return;
                }
                if (action == "cancel")
                {
                    _caller.Cancel();
                }
                else
                {
                    harness.Time.SetUtcNow(
                        ProjectionReaderHarness.Start.AddSeconds(harness.Settings.TotalReadTimeoutSeconds)
                    );
                }
            };

            try
            {
                _result = await harness.ReadAsync(_caller.Token);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_throws_on_cancellation_and_otherwise_keeps_the_failure()
        {
            if (action == "cancel")
            {
                _exception
                    .Should()
                    .BeAssignableTo<OperationCanceledException>()
                    .Which.CancellationToken.Should()
                    .Be(_caller.Token);
            }
            else
            {
                _result
                    .Should()
                    .BeOfType<EducationOrganizationProjectionReadResult.Failure>()
                    .Which.Detail.Code.Should()
                    .Be(Code.DataInvalid);
            }
        }
    }
}
