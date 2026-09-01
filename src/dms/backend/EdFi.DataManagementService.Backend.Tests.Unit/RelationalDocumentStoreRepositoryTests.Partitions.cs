// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The partition-boundary entry point on the relational repository.
/// </summary>
/// <remarks>
/// A separate file over the same fixture so these reuse the mapping-set, authorization, and command
/// builders the query tests already own; duplicating them would let the two operations be exercised
/// against different resource shapes.
/// </remarks>
public partial class Given_RelationalDocumentStoreRepositoryTests
{
    private const int DefaultRequestedPartitionCount = 4;
    private const long DefaultMinimumPartitionSize = 500L;

    private List<RelationalCommand> _capturedPartitionCommands = [];

    [Test]
    public async Task It_turns_the_boundary_statements_starting_ids_into_contiguous_inclusive_ranges()
    {
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(mappingSet);

        StubPartitionBoundaryStarts(100L, 250L, 375L);

        var result = await _sut.QueryPartitions(partitionRequest);

        result
            .Should()
            .BeOfType<PartitionResult.PartitionSuccess>()
            .Which.Ranges.Should()
            .Equal(
                new CursorRange(100L, 249L),
                new CursorRange(250L, 374L),
                new CursorRange(375L, long.MaxValue)
            );
    }

    [Test]
    public async Task It_returns_no_ranges_when_the_boundary_statement_selects_nothing()
    {
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(mappingSet);

        StubPartitionBoundaryStarts();

        var result = await _sut.QueryPartitions(partitionRequest);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();

        // The boundary command ran and found no starts, so this empty result is executed rather than
        // short-circuited. Classifying it by shape alone would report a real boundary calculation as
        // having done no database work.
        success.SelectionSkipped.Should().BeFalse();
        _capturedPartitionCommands.Should().ContainSingle();
    }

    [Test]
    public async Task It_executes_exactly_one_command_and_binds_the_requested_count_and_minimum_size()
    {
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(
            mappingSet,
            requestedPartitionCount: 7,
            minimumPartitionSize: 900L
        );

        StubPartitionBoundaryStarts(10L);

        await _sut.QueryPartitions(partitionRequest);

        var command = _capturedPartitionCommands.Should().ContainSingle().Subject;

        ParameterValue(command, "@number").Should().Be(7L);
        ParameterValue(command, "@minimumPartitionSize").Should().Be(900L);
        command.CommandText.Should().Contain("ROW_NUMBER() OVER");
        command.CommandText.Should().Contain("COUNT(*) OVER ()");
        command.CommandText.Count(character => character == ';').Should().Be(1);
    }

    // The boundary statement selects identifiers and hydrates nothing, so no document is ever
    // materialized on this path.
    [Test]
    public async Task It_neither_hydrates_documents_nor_materializes_a_response()
    {
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(mappingSet);

        StubPartitionBoundaryStarts(10L, 20L);

        await _sut.QueryPartitions(partitionRequest);

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    // Read acceleration caches hydrated documents and the candidate pages that selected them. A boundary
    // calculation ranges over the whole authorized candidate relation, so there is nothing for the
    // coordinator to serve and it must not be consulted at all.
    [Test]
    public async Task It_never_consults_read_acceleration_for_a_partition_request()
    {
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(mappingSet);

        UseReadAccelerationCoordinator(readAccelerationCoordinator);
        StubPartitionBoundaryStarts(10L);

        var result = await _sut.QueryPartitions(partitionRequest);

        result.Should().BeOfType<PartitionResult.PartitionSuccess>();
        readAccelerationCoordinator.QueryAttempts.Should().Be(0);
        readAccelerationCoordinator.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_routes_a_descriptor_partition_request_to_the_descriptor_read_handler()
    {
        var descriptorReadHandler = A.Fake<IDescriptorReadHandler>();
        var descriptorResourceInfo = CreateResourceInfo("SchoolTypeDescriptor");
        var mappingSet = CreateDescriptorOnlyMappingSet(descriptorResourceInfo);
        var expectedResult = new PartitionResult.PartitionSuccess([new CursorRange(5L, long.MaxValue)]);
        DescriptorPartitionRequest capturedRequest = null!;

        A.CallTo(() =>
                descriptorReadHandler.HandlePartitionsAsync(
                    A<DescriptorPartitionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorPartitionRequest>(0)!)
            .Returns(expectedResult);

        UseDescriptorReadHandler(descriptorReadHandler);
        StubPartitionBoundaryStarts();

        var partitionRequest = CreatePartitionRequest(
            mappingSet,
            resourceInfo: descriptorResourceInfo,
            requestedPartitionCount: 6,
            minimumPartitionSize: 750L,
            queryElements: [CreateQueryElement("codeValue", "$.codeValue", "Physical", "string")],
            changeVersionRange: new ChangeVersionRange(11L, 22L)
        );

        var result = await _sut.QueryPartitions(partitionRequest);

        result.Should().BeSameAs(expectedResult);
        capturedRequest.Resource.ResourceName.Should().Be("SchoolTypeDescriptor");
        capturedRequest.RequestedPartitionCount.Should().Be(6);
        capturedRequest.MinimumPartitionSize.Should().Be(750L);
        capturedRequest.QueryElements.Should().ContainSingle();
        capturedRequest.ChangeVersionRange.MinChangeVersion.Should().Be(11L);
        capturedRequest.ChangeVersionRange.MaxChangeVersion.Should().Be(22L);
        _capturedPartitionCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_an_intentionally_omitted_query_capability_as_a_partition_not_implemented_failure()
    {
        const string OmissionReason = "partition capability was intentionally omitted for the test fixture.";
        var partitionRequest = CreatePartitionRequest(
            CreateOmittedQueryCapabilityMappingSet(
                _schoolResourceInfo,
                new RelationalQueryCapabilityOmission(
                    RelationalQueryCapabilityOmissionKind.DescriptorResource,
                    OmissionReason
                )
            )
        );

        StubPartitionBoundaryStarts();

        var result = await _sut.QueryPartitions(partitionRequest);

        result
            .Should()
            .BeEquivalentTo(
                new PartitionResult.PartitionFailureNotImplemented(
                    "Relational query capability for resource 'Ed-Fi.School' was intentionally omitted: "
                        + OmissionReason
                )
            );
        _capturedPartitionCommands.Should().BeEmpty();
    }

    // Candidate planning proves the filter value cannot match anything before any statement is built.
    // That is an empty boundary set, not a failure, and it must cost no command.
    [Test]
    public async Task It_returns_no_ranges_without_a_command_when_planning_proves_the_filter_matches_nothing()
    {
        var partitionRequest = CreatePartitionRequest(
            CreateEducationAgencyFilterMappingSet(),
            queryElements:
            [
                CreateQueryElement(
                    "localEducationAgencyId",
                    "$.localEducationAgencyId",
                    "not-a-number",
                    "number"
                ),
            ]
        );

        StubPartitionBoundaryStarts();

        var result = await _sut.QueryPartitions(partitionRequest);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();
        success.SelectionSkipped.Should().BeTrue();
        _capturedPartitionCommands.Should().BeEmpty();
    }

    // Preprocessing proves an id filter cannot name a document before planning is even reached, which is
    // the earliest of the partition short-circuits. It must cost no command and report as skipped.
    [Test]
    public async Task It_returns_no_ranges_without_a_command_when_preprocessing_proves_the_id_filter_matches_nothing()
    {
        var partitionRequest = CreatePartitionRequest(
            CreateQuerySupportedMappingSet(
                _schoolResourceInfo,
                CreateSupportedQueryField(
                    "id",
                    "$.id",
                    "string",
                    new RelationalQueryFieldTarget.DocumentUuid()
                )
            ),
            queryElements: [CreateQueryElement("id", "$.id", "not-a-guid", "string")]
        );

        StubPartitionBoundaryStarts();

        var result = await _sut.QueryPartitions(partitionRequest);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();
        success.SelectionSkipped.Should().BeTrue();
        _capturedPartitionCommands.Should().BeEmpty();
    }

    // A caller with no usable claims can reach no candidate, which the shared authorization resolution
    // reports as an empty page. The partition equivalent is an empty boundary set, not a failure.
    [Test]
    public async Task It_returns_no_ranges_when_relationship_authorization_finds_no_usable_claims()
    {
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(
            mappingSet,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: []
        );

        StubPartitionBoundaryStarts();

        var result = await _sut.QueryPartitions(partitionRequest);

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Should().BeEmpty();

        // The skip happens inside the shared authorization resolution, which answers in QueryResult, so
        // this also proves the flag survives the query-to-partition restatement.
        success.SelectionSkipped.Should().BeTrue();
        _capturedPartitionCommands.Should().BeEmpty();
    }

    // A namespace strategy over a root with no usable Namespace column is the shared resolution's
    // security-configuration terminal. It must arrive as the partition-named failure rather than as a
    // query-named one, and it must cost no command.
    [Test]
    public async Task It_restates_a_namespace_security_configuration_terminal_as_a_partition_named_failure()
    {
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(
            mappingSet,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: []
        );

        StubPartitionBoundaryStarts();

        var result = await _sut.QueryPartitions(partitionRequest);

        result
            .Should()
            .BeOfType<PartitionResult.PartitionFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle();
        _capturedPartitionCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_binds_the_change_version_window_into_the_boundary_statement()
    {
        var mappingSet = CreateChangeVersionQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(
            mappingSet,
            changeVersionRange: new ChangeVersionRange(31L, 42L)
        );

        StubPartitionBoundaryStarts(10L);

        await _sut.QueryPartitions(partitionRequest);

        var command = _capturedPartitionCommands.Should().ContainSingle().Subject;

        ParameterValue(command, "@minChangeVersion").Should().Be(31L);
        ParameterValue(command, "@maxChangeVersion").Should().Be(42L);
    }

    // The statement orders its starts, so a non-ascending set means the compiled SQL changed. Reporting
    // it keeps a duplicated or inverted range from reaching a client as a walkable one.
    [Test]
    public async Task It_reports_an_unknown_failure_when_the_boundary_statement_returns_non_ascending_starts()
    {
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(mappingSet);

        StubPartitionBoundaryStarts(400L, 100L);

        var result = await _sut.QueryPartitions(partitionRequest);

        result
            .Should()
            .BeOfType<PartitionResult.UnknownPartitionFailure>()
            .Which.FailureMessage.Should()
            .Contain("strictly ascending");
    }

    // The custom-view probe is the one command the boundary path is allowed to issue besides the
    // boundary statement itself — the explicit exception to the one-command shape asserted above. It has
    // to run first: the boundary statement selects through the configured views, so a missing or
    // non-conforming view must be reported as a validation failure rather than as whatever the provider
    // says when the boundary statement reaches it.
    [Test]
    public async Task It_validates_custom_views_before_executing_the_boundary_command()
    {
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var partitionRequest = CreatePartitionRequest(
            mappingSet,
            authorizationStrategyEvaluators: [CreateAuthorizationStrategyEvaluator(CustomViewStrategyName)]
        );

        StubPartitionBoundaryStarts(100L);
        StubPartitionCustomViewValidation();

        var result = await _sut.QueryPartitions(partitionRequest);

        result.Should().BeOfType<PartitionResult.PartitionSuccess>();
        _capturedPartitionCommands.Should().HaveCount(2);
        _capturedPartitionCommands[0].CommandText.Should().Contain(CustomViewStrategyName);
        _capturedPartitionCommands[0].CommandText.Should().NotContain("ROW_NUMBER() OVER");
        _capturedPartitionCommands[1].CommandText.Should().Contain("ROW_NUMBER() OVER");
    }

    // Validation and the boundary statement are separate round trips against the same views, so a view
    // dropped, revoked, or broken in between raises only at execution. That failure must keep the
    // custom-view validation contract GET-many reports for the same condition instead of escaping as an
    // unhandled provider error.
    [Test]
    public async Task It_relabels_a_provider_error_under_a_custom_view()
    {
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var databaseException = new StubDbException("custom view does not exist");
        var partitionRequest = CreatePartitionRequest(
            mappingSet,
            authorizationStrategyEvaluators: [CreateAuthorizationStrategyEvaluator(CustomViewStrategyName)]
        );

        StubPartitionBoundaryStarts();
        StubPartitionCustomViewValidation();

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<IReadOnlyList<long>>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);

        var action = () => _sut.QueryPartitions(partitionRequest);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    [Test]
    public async Task It_rejects_a_null_partition_request()
    {
        var action = () => _sut.QueryPartitions(null!);

        await action.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// The root EdOrg-subject mapping set with an Int64 root-column filter registered, so a filter value
    /// that cannot be represented as that column's scalar kind reaches candidate planning.
    /// </summary>
    private static MappingSet CreateEducationAgencyFilterMappingSet() =>
        CreateQuerySupportedMappingSet(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "localEducationAgencyId",
                "$.localEducationAgencyId",
                "number",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("LocalEducationAgencyId"))
            )
        );

    private void UseDescriptorReadHandler(IDescriptorReadHandler descriptorReadHandler)
    {
        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            new ThrowingDescriptorWriteHandler(),
            descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _ownershipAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );
    }

    /// <summary>
    /// Answers the custom-view probe and records it into the same ordered list the boundary command is
    /// recorded into, so a test can assert which of the two ran first rather than only that both ran.
    /// Installed after <see cref="StubPartitionBoundaryStarts" />, which resets that list.
    /// </summary>
    private void StubPartitionCustomViewValidation() =>
        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => _capturedPartitionCommands.Add(call.GetArgument<RelationalCommand>(0)!))
            .Returns(Task.FromResult(true));

    /// <summary>
    /// Answers the boundary command with <paramref name="ascendingStarts" /> and records every command
    /// the partition path issues, so a test can assert both what was bound and how many commands ran.
    /// Every partition test installs this, including the ones that expect no command at all: the
    /// recording list is what proves the difference.
    /// </summary>
    private void StubPartitionBoundaryStarts(params long[] ascendingStarts)
    {
        _capturedPartitionCommands = [];

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<IReadOnlyList<long>>>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand command,
                    Func<IRelationalCommandReader, CancellationToken, Task<IReadOnlyList<long>>> readAsync,
                    CancellationToken cancellationToken
                ) =>
                {
                    _capturedPartitionCommands.Add(command);

                    return readAsync(
                        new InMemoryRelationalCommandReader([
                            InMemoryRelationalResultSet.Create([
                                .. ascendingStarts.Select(start =>
                                    RelationalAccessTestData.CreateRow(("DocumentId", start))
                                ),
                            ]),
                        ]),
                        cancellationToken
                    );
                }
            );
    }

    private static IPartitionRequest CreatePartitionRequest(
        MappingSet mappingSet,
        QueryElement[]? queryElements = null,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        IReadOnlyList<string>? namespacePrefixes = null,
        ResourceInfo? resourceInfo = null,
        ChangeVersionRange? changeVersionRange = null,
        int requestedPartitionCount = DefaultRequestedPartitionCount,
        long minimumPartitionSize = DefaultMinimumPartitionSize
    )
    {
        var partitionRequest = A.Fake<IPartitionRequest>();

        A.CallTo(() => partitionRequest.ResourceInfo).Returns(resourceInfo ?? _schoolResourceInfo);
        A.CallTo(() => partitionRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => partitionRequest.QueryElements).Returns(queryElements ?? []);
        A.CallTo(() => partitionRequest.AuthorizationStrategyEvaluators)
            .Returns(authorizationStrategyEvaluators ?? []);
        A.CallTo(() => partitionRequest.AuthorizationContext)
            .Returns(
                new RelationalAuthorizationContext(
                    claimEducationOrganizationIds ?? [],
                    namespacePrefixes ?? []
                )
            );
        A.CallTo(() => partitionRequest.ChangeVersionRange)
            .Returns(changeVersionRange ?? ChangeVersionRange.None);
        A.CallTo(() => partitionRequest.RequestedPartitionCount).Returns(requestedPartitionCount);
        A.CallTo(() => partitionRequest.MinimumPartitionSize).Returns(minimumPartitionSize);
        A.CallTo(() => partitionRequest.TraceId).Returns(new TraceId("partition-trace"));
        A.CallTo(() => partitionRequest.TenantKey).Returns(string.Empty);

        return partitionRequest;
    }
}
