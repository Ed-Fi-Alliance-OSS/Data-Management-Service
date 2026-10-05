// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Tests.Unit.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.Tests.Unit.Middleware;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using SetResult = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

/// <summary>
/// A set reader that answers with a configured result and records what it was asked.
/// </summary>
internal sealed class StubProjectionSetReader : IEducationOrganizationProjectionSetReader
{
    public Func<CancellationToken, SetResult> Answer { get; set; } = _ => new SetResult.Set([]);

    public List<EducationOrganizationProjectionSetReadRequest> Requests { get; } = [];

    public Task<SetResult> ReadSetAsync(
        EducationOrganizationProjectionSetReadRequest request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request);
        return Task.FromResult(Answer(cancellationToken));
    }
}

/// <summary>
/// Records the stages and checkpoints the handler reports, and cancels the request at a named
/// checkpoint when asked to.
/// </summary>
internal sealed class RecordingProjectionObserver : IProjectionProcessingObserver
{
    public List<ProjectionProcessingStage> Stages { get; } = [];

    public List<(ProjectionProcessingStage Stage, int Rows)> Checkpoints { get; } = [];

    public (ProjectionProcessingStage Stage, int Rows)? CancelAt { get; set; }

    public CancellationTokenSource Cancellation { get; } = new();

    public void Stage(ProjectionProcessingStage stage) => Stages.Add(stage);

    public void Checkpoint(ProjectionProcessingStage stage, int rowsProcessed)
    {
        Checkpoints.Add((stage, rowsProcessed));

        if (CancelAt == (stage, rowsProcessed))
        {
            Cancellation.Cancel();
        }
    }
}

/// <summary>
/// Shared arrangement: the contract's worked example request (tenant <c>Tenant_255901</c>, qualifiers
/// <c>255901/2025</c>, data store 3788, contract version v1) at the worked example's walk time, the
/// handler built with the default settings, a stub reader and a recording observer.
/// </summary>
public abstract class EducationOrganizationProjectionHandlerTests
{
    protected const int DataStoreId = 3788;

    internal static readonly MappingSet MappingSet =
        ResolveEducationOrganizationProjectionMappingSetMiddlewareTests.CreateMappingSet(SqlDialect.Pgsql);

    internal EducationOrganizationProjectionSettings Settings { get; set; } = new();

    internal FakeTimeProvider Clock { get; private set; } = new();

    internal StubProjectionSetReader Reader { get; private set; } = new();

    internal RecordingProjectionObserver Observer { get; private set; } = new();

    internal RecordingLogger<EducationOrganizationProjectionHandler> Logger { get; private set; } = new();

    internal RequestInfo RequestInfo { get; private set; } = null!;

    protected bool NextCalled { get; private set; }

    [SetUp]
    public void ResetArrangement()
    {
        Settings = new EducationOrganizationProjectionSettings();
        Clock = new FakeTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(ProjectionCursorFixtures.WorkedWalkIssuedAt)
        );
        Reader = new StubProjectionSetReader();
        Observer = new RecordingProjectionObserver();
        Logger = new RecordingLogger<EducationOrganizationProjectionHandler>();
        NextCalled = false;
    }

    protected void Answer(params EducationOrganizationProjectionRow[] rows) =>
        Reader.Answer = _ => new SetResult.Set(rows);

    internal static ProjectionCursor WorkedCursor(
        long lastId,
        string digest = ProjectionCursorFixtures.WorkedDigest
    ) =>
        new(
            DataStoreId,
            lastId,
            digest,
            ProjectionCursorFixtures.WorkedWalkIssuedAt,
            ProjectionCursorFixtures.WorkedBinding
        );

    internal async Task Execute(int limit = 2, ProjectionCursor? cursor = null)
    {
        FrontendRequest frontendRequest = new(
            Path: "/Tenant_255901/255901/2025/management/education-organizations",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("projection-handler"),
            RouteQualifiers: ProjectionCursorFixtures.WorkedQualifiers(),
            Tenant: "Tenant_255901"
        );

        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IEducationOrganizationProjectionSetReader>(Reader)
            .BuildServiceProvider();

        RequestInfo = new RequestInfo(frontendRequest, RequestMethod.GET, services)
        {
            EducationOrganizationProjectionRequest = new EducationOrganizationProjectionRequest(
                DataStoreId,
                limit,
                ProjectionContractVersions.V1,
                ProjectionCursorFixtures.WorkedBinding,
                cursor
            ),
            MappingSet = MappingSet,
            RequestCancellationToken = Observer.Cancellation.Token,
        };

        EducationOrganizationProjectionHandler handler = new(Settings, Clock, Observer, Logger);

        await handler.Execute(
            RequestInfo,
            () =>
            {
                NextCalled = true;
                return Task.CompletedTask;
            }
        );
    }

    protected IFrontendResponse Response => RequestInfo.FrontendResponse;

    protected JsonObject Body => Response.Body!.AsObject();

    protected long[] ItemIds =>
        [.. Body["items"]!.AsArray().Select(item => item!["educationOrganizationId"]!.GetValue<long>())];

    internal ProjectionCursor NextCursor()
    {
        ProjectionCursorCodec
            .TryDecode(Body["nextCursor"]!.GetValue<string>(), out ProjectionCursor? cursor)
            .Should()
            .BeTrue();
        return cursor!;
    }

    protected void ShouldAnswerProblem(int status, string type)
    {
        Response.StatusCode.Should().Be(status);
        Response.ContentType.Should().Be("application/problem+json");
        Body["type"]!.GetValue<string>().Should().Be(type);
        Body["errors"]!.AsArray().Should().BeEmpty();
    }

    /// <summary>
    /// A checked-in contract example, located by walking up from the test output directory to the
    /// repository's design folder.
    /// </summary>
    protected static JsonNode ContractExample(string fileName)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "reference",
                "design",
                "edorg-projection-DMS-1440",
                "contract",
                "examples",
                fileName
            );

            if (File.Exists(candidate))
            {
                return JsonNode.Parse(File.ReadAllText(candidate))!;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Contract example '{fileName}' was not found above the test directory."
        );
    }

    protected const string ProblemPrefix = "urn:ed-fi:api:education-organization-projection";

    [TestFixture]
    [Parallelizable]
    public class Given_The_First_Page_Of_The_Worked_Example : EducationOrganizationProjectionHandlerTests
    {
        [SetUp]
        public async Task Setup()
        {
            // Not the defaults, so the read request is shown to carry the configured values.
            Settings = new EducationOrganizationProjectionSettings
            {
                MaxProjectionRows = 1234,
                ReadLockTimeoutSeconds = 7,
                ReadCommandTimeoutSeconds = 42,
            };
            Answer(ProjectionRows.WorkedExample());
            await Execute(limit: 2);
        }

        [Test]
        public void It_answers_the_contract_success_page_exactly() =>
            JsonNode
                .DeepEquals(Body, ContractExample("success-page.json"))
                .Should()
                .BeTrue(Body.ToJsonString());

        [Test]
        public void It_answers_200_as_json()
        {
            Response.StatusCode.Should().Be(200);
            Response.ContentType.Should().Be("application/json");
        }

        [Test]
        public void It_asks_the_reader_for_the_whole_set_under_the_configured_limits()
        {
            Reader
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new EducationOrganizationProjectionSetReadRequest(MappingSet, 1234, 7, 42));
        }

        [Test]
        public void It_passes_through_every_stage_in_order() =>
            Observer
                .Stages.Should()
                .Equal(
                    ProjectionProcessingStage.Validation,
                    ProjectionProcessingStage.Precedence,
                    ProjectionProcessingStage.Hashing,
                    ProjectionProcessingStage.Slicing,
                    ProjectionProcessingStage.Publishing
                );

        [Test]
        public void It_starts_the_walk_clock_at_the_first_page() =>
            NextCursor().WalkIssuedAtUnixSeconds.Should().Be(ProjectionCursorFixtures.WorkedWalkIssuedAt);

        [Test]
        public void It_is_the_terminal_step() => NextCalled.Should().BeFalse();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Last_Page_Of_The_Worked_Example : EducationOrganizationProjectionHandlerTests
    {
        [SetUp]
        public async Task Setup()
        {
            Answer(ProjectionRows.WorkedExample());
            Clock.Advance(TimeSpan.FromMinutes(30));
            await Execute(limit: 2, cursor: WorkedCursor(lastId: 101001));
        }

        [Test]
        public void It_answers_the_contract_last_page_exactly() =>
            JsonNode
                .DeepEquals(Body, ContractExample("success-last-page.json"))
                .Should()
                .BeTrue(Body.ToJsonString());
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Continuation_Page_Later_In_The_Walk : EducationOrganizationProjectionHandlerTests
    {
        [SetUp]
        public async Task Setup()
        {
            Answer(ProjectionRows.WorkedExample());
            Clock.Advance(TimeSpan.FromMinutes(10));
            await Execute(limit: 2, cursor: WorkedCursor(lastId: 10));
        }

        [Test]
        public void It_returns_the_items_after_the_position() => ItemIds.Should().Equal(100, 101);

        [Test]
        public void It_moves_the_position_to_the_last_item() =>
            NextCursor().LastEducationOrganizationId.Should().Be(101);

        [Test]
        public void It_carries_the_walk_timestamp_unchanged() =>
            NextCursor().WalkIssuedAtUnixSeconds.Should().Be(ProjectionCursorFixtures.WorkedWalkIssuedAt);

        [Test]
        public void It_carries_the_first_page_digest() =>
            NextCursor().Digest.Should().Be(ProjectionCursorFixtures.WorkedDigest);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Page_That_Reaches_The_Last_Item_Exactly : EducationOrganizationProjectionHandlerTests
    {
        [Test]
        public async Task It_ends_the_walk_without_an_empty_continuation_page()
        {
            Answer(ProjectionRows.WorkedExample());

            await Execute(limit: 2, cursor: WorkedCursor(lastId: 100001));

            ItemIds.Should().Equal(101001, 900001);
            Body["nextCursor"].Should().BeNull();
        }

        [Test]
        public async Task It_ends_a_first_page_that_holds_the_whole_set()
        {
            Answer(ProjectionRows.WorkedExample());

            await Execute(limit: 7);

            ItemIds.Should().HaveCount(7);
            Body["nextCursor"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Empty_Set : EducationOrganizationProjectionHandlerTests
    {
        [SetUp]
        public async Task Setup()
        {
            Answer();
            await Execute(limit: 2);
        }

        [Test]
        public void It_answers_the_contract_empty_page_apart_from_the_data_store_id()
        {
            JsonNode expected = ContractExample("empty.json");
            expected["dataStoreId"] = DataStoreId;

            JsonNode.DeepEquals(Body, expected).Should().BeTrue(Body.ToJsonString());
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Negative_And_Zero_Identifiers : EducationOrganizationProjectionHandlerTests
    {
        private static EducationOrganizationProjectionRow[] Rows() =>
            [
                ProjectionRows.Sea(long.MinValue),
                ProjectionRows.Sea(-1),
                ProjectionRows.Sea(0),
                ProjectionRows.Sea(3),
            ];

        [Test]
        public async Task It_includes_them_on_the_first_page()
        {
            Answer(Rows());

            await Execute(limit: 3);

            ItemIds.Should().Equal(long.MinValue, -1, 0);
            NextCursor().LastEducationOrganizationId.Should().Be(0);
        }

        [Test]
        public async Task It_continues_strictly_after_a_zero_position()
        {
            Answer(Rows());
            await Execute(limit: 3);
            ProjectionCursor cursor = NextCursor();

            await Execute(limit: 3, cursor: cursor);

            ItemIds.Should().Equal(3);
            Body["nextCursor"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Set_Whose_Projected_Content_Changed : EducationOrganizationProjectionHandlerTests
    {
        [SetUp]
        public async Task Setup()
        {
            EducationOrganizationProjectionRow[] rows = ProjectionRows.WorkedExample();
            rows[6] = ProjectionRows.School(900001, name: "Independent Academy Renamed");
            Answer(rows);

            await Execute(limit: 2, cursor: WorkedCursor(lastId: 10));
        }

        [Test]
        public void It_answers_projection_changed() =>
            ShouldAnswerProblem(409, $"{ProblemPrefix}:projection-changed");

        [Test]
        public void It_does_not_slice_a_page() =>
            Observer.Stages.Should().NotContain(ProjectionProcessingStage.Slicing);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Change_The_Projection_Does_Not_Return : EducationOrganizationProjectionHandlerTests
    {
        /// <summary>
        /// A reference precedence does not select changes nothing projected, so the digest and the page
        /// are those of the unchanged set.
        /// </summary>
        [Test]
        public async Task It_answers_the_page()
        {
            EducationOrganizationProjectionRow[] rows = ProjectionRows.WorkedExample();
            rows[3] = ProjectionRows.Lea(101, parent: 100, sea: 1, name: "Grand Bend North ISD");
            Answer(rows);

            await Execute(limit: 2, cursor: WorkedCursor(lastId: 10));

            Response.StatusCode.Should().Be(200);
            ItemIds.Should().Equal(100, 101);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Cursor_At_The_End_Of_An_Unchanged_Set : EducationOrganizationProjectionHandlerTests
    {
        [SetUp]
        public async Task Setup()
        {
            Answer(ProjectionRows.WorkedExample());
            await Execute(limit: 2, cursor: WorkedCursor(lastId: 900001));
        }

        [Test]
        public void It_refuses_the_cursor_rather_than_answer_an_empty_page() =>
            ShouldAnswerProblem(400, $"{ProblemPrefix}:invalid-cursor");
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Read_That_Did_Not_Return_A_Set : EducationOrganizationProjectionHandlerTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData(
                new SetResult.TooLarge(50000),
                409,
                $"{ProblemPrefix}:projection-too-large"
            ).SetName("It_answers_projection_too_large_for_TooLarge");
            yield return new TestCaseData(
                new SetResult.MappingIncompatible(
                    new EducationOrganizationProjectionMappingIncompatibility(
                        EducationOrganizationProjectionMappingIncompatibilityReason.ArmMissing,
                        "School"
                    )
                ),
                409,
                $"{ProblemPrefix}:projection-unsupported"
            ).SetName("It_answers_projection_unsupported_for_MappingIncompatible");
            yield return new TestCaseData(
                new SetResult.SchemaIncompatible(
                    EducationOrganizationProjectionSchemaIncompatibilityReason.SchemaObjectMissing,
                    "42703"
                ),
                409,
                $"{ProblemPrefix}:target-schema-incompatible"
            ).SetName("It_answers_target_schema_incompatible_for_SchemaIncompatible");
            yield return new TestCaseData(
                new SetResult.TargetUnavailable(
                    EducationOrganizationProjectionReadStage.Execute,
                    "PostgresException 40P01"
                ),
                503,
                $"{ProblemPrefix}:target-unavailable"
            ).SetName("It_answers_target_unavailable_for_TargetUnavailable");
        }

        [TestCaseSource(nameof(Cases))]
        public async Task It_translates_the_result(SetResult result, int status, string type)
        {
            Reader.Answer = _ => result;

            await Execute();

            ShouldAnswerProblem(status, type);
            Observer.Stages.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Contradiction_Outside_The_Requested_Page
        : EducationOrganizationProjectionHandlerTests
    {
        [SetUp]
        public async Task Setup()
        {
            Answer([
                .. ProjectionRows.WorkedExample(),
                ProjectionRows.Lea(2_000_000, parent: 2_000_001),
                ProjectionRows.Lea(2_000_001, parent: 2_000_000),
            ]);

            await Execute(limit: 1);
        }

        [Test]
        public void It_fails_the_first_page() =>
            ShouldAnswerProblem(409, $"{ProblemPrefix}:projection-data-invalid");

        [Test]
        public void It_fails_before_hashing() =>
            Observer.Stages.Should().Equal(ProjectionProcessingStage.Validation);

        [Test]
        public void It_logs_the_reason_but_no_identifier() =>
            Logger
                .Records.Should()
                .Contain(record => record.Message.Contains("ParentCycle"))
                .And.NotContain(record => record.Message.Contains("2000000"));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Name_With_A_Lone_Surrogate : EducationOrganizationProjectionHandlerTests
    {
        // The code unit travels as a number: a lone surrogate in a test-case string is replaced with
        // U+FFFD on its way to the test host, which would make the case pass without the check.
        [TestCase(0xD83D, TestName = "It_refuses_a_lone_high_surrogate_before_hashing")]
        [TestCase(0xDE00, TestName = "It_refuses_a_lone_low_surrogate_before_hashing")]
        public async Task It_refuses_the_set_before_hashing(int surrogate)
        {
            EducationOrganizationProjectionRow[] rows = ProjectionRows.WorkedExample();
            rows[6] = ProjectionRows.School(
                900001,
                name: "Independent",
                shortName: ((char)surrogate).ToString()
            );
            Answer(rows);

            await Execute();

            ShouldAnswerProblem(409, $"{ProblemPrefix}:projection-data-invalid");
            Observer.Stages.Should().NotContain(ProjectionProcessingStage.Hashing);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Stored_Discriminator_Outside_The_Allowlist
        : EducationOrganizationProjectionHandlerTests
    {
        [Test]
        public async Task It_answers_projection_unsupported()
        {
            Answer(
                new EducationOrganizationProjectionRow(
                    1,
                    "Ed-Fi:PostSecondaryInstitution",
                    "P",
                    null,
                    null,
                    null,
                    null,
                    null
                )
            );

            await Execute();

            ShouldAnswerProblem(409, $"{ProblemPrefix}:projection-unsupported");
            Body.ToJsonString().Should().NotContain("Ed-Fi:");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cancellation_At_A_Validation_Checkpoint : EducationOrganizationProjectionHandlerTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            Answer([.. Enumerable.Range(1, 5000).Select(id => ProjectionRows.Sea(id))]);
            Observer.CancelAt = (ProjectionProcessingStage.Validation, 2000);
            _act = () => Execute();
        }

        [Test]
        public async Task It_throws_OperationCanceledException() =>
            await _act.Should().ThrowAsync<OperationCanceledException>();

        [Test]
        public async Task It_never_enters_hashing()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();

            Observer.Stages.Should().Equal(ProjectionProcessingStage.Validation);
            Observer
                .Checkpoints.Should()
                .Equal(
                    (ProjectionProcessingStage.Validation, 1000),
                    (ProjectionProcessingStage.Validation, 2000)
                );
        }

        [Test]
        public async Task It_writes_no_response()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();

            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cancellation_At_A_Hashing_Checkpoint : EducationOrganizationProjectionHandlerTests
    {
        [Test]
        public async Task It_stops_before_slicing_and_writes_no_response()
        {
            Answer([.. Enumerable.Range(1, 3000).Select(id => ProjectionRows.Sea(id))]);
            Observer.CancelAt = (ProjectionProcessingStage.Hashing, 1000);

            Func<Task> act = () => Execute();

            await act.Should().ThrowAsync<OperationCanceledException>();
            Observer.Stages.Should().NotContain(ProjectionProcessingStage.Slicing);
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Read_Cancelled_By_The_Caller : EducationOrganizationProjectionHandlerTests
    {
        [Test]
        public async Task It_propagates_the_cancellation_and_writes_no_response()
        {
            Reader.Answer = cancellationToken => throw new OperationCanceledException(cancellationToken);

            Func<Task> act = () => Execute();

            await act.Should().ThrowAsync<OperationCanceledException>();
            Observer.Stages.Should().BeEmpty();
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }
}
