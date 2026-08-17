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

        result.Should().BeOfType<PartitionResult.PartitionSuccess>().Which.Ranges.Should().BeEmpty();
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
        long minimumPartitionSize = 500L
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
            new RelationalAuthorizationContext([], namespacePrefixes ?? []),
            changeVersionRange
        );
    }
}
