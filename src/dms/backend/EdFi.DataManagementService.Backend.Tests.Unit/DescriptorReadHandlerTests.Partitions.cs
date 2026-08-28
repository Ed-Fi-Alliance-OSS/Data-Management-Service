// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The descriptor partition-boundary entry point.
/// </summary>
/// <remarks>
/// A separate file over the same fixture so these run against the mapping set, capability, and
/// authorization builders the descriptor GET-many tests already own. That is what makes an assertion
/// here evidence that the two operations see the same descriptor shape.
/// </remarks>
public partial class Given_DescriptorReadHandler
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_turns_the_descriptor_boundary_starts_into_contiguous_inclusive_ranges(
        SqlDialect dialect
    )
    {
        var commandExecutor = CreateBoundaryCommandExecutor(dialect, 110L, 130L, 150L);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(CreatePartitionRequest(dialect));

        result
            .Should()
            .BeOfType<PartitionResult.PartitionSuccess>()
            .Which.Ranges.Should()
            .Equal(
                new CursorRange(110L, 129L),
                new CursorRange(130L, 149L),
                new CursorRange(150L, long.MaxValue)
            );
        commandExecutor.Commands.Should().ContainSingle();
    }

    // The ResourceKeyId discriminator, the descriptor filter, and both partition values must all reach
    // the one command. A boundary set built without the discriminator would span every descriptor type.
    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Descriptor\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Descriptor]")]
    public async Task It_binds_the_resource_key_the_filter_and_both_partition_values(
        SqlDialect dialect,
        string expectedDescriptorTableFragment
    )
    {
        var commandExecutor = CreateBoundaryCommandExecutor(dialect, 110L);
        var sut = CreateHandler(commandExecutor);

        await sut.HandlePartitionsAsync(
            CreatePartitionRequest(
                dialect,
                queryElements:
                [
                    new QueryElement("codeValue", [new JsonPath("$.codeValue")], "Alternative", "string"),
                ],
                requestedPartitionCount: 9,
                minimumPartitionSize: 640L
            )
        );

        var command = commandExecutor.Commands.Should().ContainSingle().Subject;

        command.CommandText.Should().Contain(expectedDescriptorTableFragment);
        command.CommandText.Should().Contain("ROW_NUMBER() OVER");
        command.CommandText.Count(character => character == ';').Should().Be(1);
        BoundaryParameterValue(command, "@resourceKeyId").Should().Be((short)13);
        BoundaryParameterValue(command, "@codeValue").Should().Be("Alternative");
        BoundaryParameterValue(command, "@number").Should().Be(9L);
        BoundaryParameterValue(command, "@minimumPartitionSize").Should().Be(640L);
    }

    [Test]
    public async Task It_returns_no_ranges_when_the_descriptor_boundary_statement_selects_nothing()
    {
        var commandExecutor = CreateBoundaryCommandExecutor(SqlDialect.Pgsql);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(CreatePartitionRequest(SqlDialect.Pgsql));

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();

        // The boundary command ran and found no starts, so this empty result is executed rather than
        // short-circuited.
        success.SelectionSkipped.Should().BeFalse();
        commandExecutor.Commands.Should().ContainSingle();
    }

    // Preprocessing proves an id filter cannot name a descriptor before a boundary statement is built,
    // so the request costs no command and reports as a skipped selection.
    [Test]
    public async Task It_returns_no_ranges_without_a_command_when_preprocessing_proves_the_id_filter_matches_nothing()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(
            CreatePartitionRequest(
                SqlDialect.Pgsql,
                queryElements: [new QueryElement("id", [new JsonPath("$.id")], "not-a-guid", "string")]
            )
        );

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();
        success.SelectionSkipped.Should().BeTrue();
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_binds_the_change_version_window_into_the_descriptor_boundary_statement()
    {
        var commandExecutor = CreateBoundaryCommandExecutor(SqlDialect.Pgsql, 110L);
        var sut = CreateHandler(commandExecutor);

        await sut.HandlePartitionsAsync(
            CreatePartitionRequest(SqlDialect.Pgsql, changeVersionRange: new ChangeVersionRange(55L, 66L))
        );

        var command = commandExecutor.Commands.Should().ContainSingle().Subject;

        BoundaryParameterValue(command, "@minChangeVersion").Should().Be(55L);
        BoundaryParameterValue(command, "@maxChangeVersion").Should().Be(66L);
    }

    [Test]
    public async Task It_restates_a_namespace_denial_as_a_partition_named_failure_without_executing_sql()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(
            CreatePartitionRequest(
                SqlDialect.Pgsql,
                authorizationStrategyEvaluators:
                [
                    CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                ],
                namespacePrefixes: []
            )
        );

        result.Should().BeOfType<PartitionResult.PartitionFailureNamespaceNotAuthorized>();
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_fails_closed_for_descriptor_partition_authorization_without_executing_sql()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(
            CreatePartitionRequest(
                SqlDialect.Pgsql,
                authorizationStrategyEvaluators:
                [
                    CreateAuthorizationStrategyEvaluator(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    ),
                ]
            )
        );

        result.Should().BeOfType<PartitionResult.PartitionFailureNotImplemented>();
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_an_intentionally_omitted_descriptor_query_capability_as_not_implemented()
    {
        const string OmissionReason =
            "descriptor partition support was intentionally omitted for the test fixture.";
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(
            CreatePartitionRequest(
                SqlDialect.Pgsql,
                descriptorQueryCapability: CreateOmittedDescriptorQueryCapability(OmissionReason)
            )
        );

        result
            .Should()
            .BeOfType<PartitionResult.PartitionFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(OmissionReason);
        commandExecutor.Commands.Should().BeEmpty();
    }

    // The statement orders its starts, so a non-ascending set means the compiled SQL changed.
    [Test]
    public async Task It_reports_an_unknown_failure_for_non_ascending_descriptor_boundary_starts()
    {
        var commandExecutor = CreateBoundaryCommandExecutor(SqlDialect.Pgsql, 150L, 110L);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(CreatePartitionRequest(SqlDialect.Pgsql));

        result
            .Should()
            .BeOfType<PartitionResult.UnknownPartitionFailure>()
            .Which.FailureMessage.Should()
            .Contain("strictly ascending");
    }

    // Read acceleration caches hydrated descriptor rows and the candidate pages that selected them, and
    // a boundary calculation produces neither.
    [Test]
    public async Task It_never_consults_read_acceleration_for_a_descriptor_partition_request()
    {
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = CreateBoundaryCommandExecutor(SqlDialect.Pgsql, 110L);
        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator);

        var result = await sut.HandlePartitionsAsync(CreatePartitionRequest(SqlDialect.Pgsql));

        result.Should().BeOfType<PartitionResult.PartitionSuccess>();
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                readAccelerationCoordinator.GetByIdAsync(
                    A<DocumentCacheReadAccelerationGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    // The non-provider fault set the page path catches, asserted here because a condition both
    // operations can reach must leave the backend as the same kind of result. One that escaped would
    // reach the client as the generic unhandled 500 rather than the logged problem+json unknown
    // failure the collection GET produces for the identical condition.
    [TestCaseSource(nameof(_boundaryFaultsReportedAsUnknownFailures))]
    public async Task It_reports_a_boundary_fault_as_an_unknown_partition_failure(Exception fault)
    {
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();

        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<IReadOnlyList<long>>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(fault);

        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(CreatePartitionRequest(SqlDialect.Pgsql));

        result
            .Should()
            .BeOfType<PartitionResult.UnknownPartitionFailure>()
            .Which.FailureMessage.Should()
            .Be(fault.Message);
    }

    private static readonly Exception[] _boundaryFaultsReportedAsUnknownFailures =
    [
        new NotSupportedException("boundary statement is not supported"),
        new DescriptorReadInvariantException("boundary reader invariant broken"),
        new InvalidOperationException("boundary parameter binding is incomplete"),
        new ArgumentException("boundary binding kind is unsupported"),
        new KeyNotFoundException("boundary plan is missing a key"),
    ];

    // The custom-view probe is the one command the boundary path is allowed to issue besides the
    // boundary statement itself, and it has to run first: the boundary statement selects through the
    // configured views, so a missing or non-conforming view must be reported as a validation failure
    // rather than as whatever the provider says when the boundary statement hits it.
    [TestCase(SqlDialect.Pgsql, "\"auth\".\"SchoolTypeDescriptorWithCustomViewProviderTest\"")]
    [TestCase(SqlDialect.Mssql, "[auth].[SchoolTypeDescriptorWithCustomViewProviderTest]")]
    public async Task It_validates_custom_views_before_executing_the_boundary_command(
        SqlDialect dialect,
        string expectedViewFragment
    )
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor(
            [
                new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        RelationalAccessTestData.CreateRow(("DocumentId", 110L))
                    ),
                ]),
            ],
            dialect
        );
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(
            CreatePartitionRequest(
                dialect,
                authorizationStrategyEvaluators:
                [
                    CreateAuthorizationStrategyEvaluator("SchoolTypeDescriptorWithCustomViewProviderTest"),
                ]
            )
        );

        result.Should().BeOfType<PartitionResult.PartitionSuccess>();
        commandExecutor.Commands.Should().HaveCount(2);
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedViewFragment);
        commandExecutor.Commands[0].CommandText.Should().NotContain("ROW_NUMBER() OVER");
        commandExecutor.Commands[1].CommandText.Should().Contain(expectedViewFragment);
        commandExecutor.Commands[1].CommandText.Should().Contain("ROW_NUMBER() OVER");
    }

    // Validation and the boundary statement are separate round trips against the same views, so a view
    // dropped, revoked, or broken in between raises only at execution. That failure must keep the
    // custom-view validation contract GET-many reports for the same condition instead of escaping as an
    // unhandled provider error.
    [Test]
    public async Task It_relabels_a_provider_error_under_a_custom_view()
    {
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var databaseException = new StubDbException("custom view does not exist");

        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<IReadOnlyList<long>>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);

        var sut = CreateHandler(commandExecutor);

        var action = () =>
            sut.HandlePartitionsAsync(
                CreatePartitionRequest(
                    SqlDialect.Pgsql,
                    authorizationStrategyEvaluators:
                    [
                        CreateAuthorizationStrategyEvaluator(
                            "SchoolTypeDescriptorWithCustomViewProviderTest"
                        ),
                    ]
                )
            );

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    [Test]
    public async Task It_rejects_a_null_descriptor_partition_request()
    {
        var sut = CreateHandler(new InMemoryRelationalCommandExecutor([]));

        var action = () => sut.HandlePartitionsAsync(null!);

        await action.Should().ThrowAsync<ArgumentNullException>();
    }

    private static object? BoundaryParameterValue(RelationalCommand command, string parameterName) =>
        command.Parameters.Single(parameter => parameter.Name == parameterName).Value;

    private static InMemoryRelationalCommandExecutor CreateBoundaryCommandExecutor(
        SqlDialect dialect,
        params long[] ascendingStarts
    ) =>
        new(
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create([
                        .. ascendingStarts.Select(start =>
                            RelationalAccessTestData.CreateRow(("DocumentId", start))
                        ),
                    ]),
                ]),
            ],
            dialect
        );

    private static DescriptorPartitionRequest CreatePartitionRequest(
        SqlDialect dialect,
        QueryElement[]? queryElements = null,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null,
        string[]? namespacePrefixes = null,
        DescriptorQueryCapability? descriptorQueryCapability = null,
        ChangeVersionRange? changeVersionRange = null,
        int requestedPartitionCount = 4,
        long minimumPartitionSize = 500L,
        PageOrderingMode pageOrderingMode = PageOrderingMode.DocumentId
    )
    {
        var mappingSet = CreateQueryMappingSet(
            dialect,
            descriptorQueryCapability ?? CreateSupportedDescriptorQueryCapability()
        );

        return new DescriptorPartitionRequest(
            mappingSet,
            _descriptorResource,
            queryElements ?? [],
            authorizationStrategyEvaluators ?? [],
            requestedPartitionCount,
            minimumPartitionSize,
            new TraceId("descriptor-partition-trace"),
            pageOrderingMode,
            new RelationalAuthorizationContext([], namespacePrefixes ?? []),
            changeVersionRange
        );
    }
}
