// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

public partial class Given_RelationalDocumentStoreRepositoryTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_maps_get_hydration_exhaustion_to_unknown_failure(bool selectedCacheFallback)
    {
        var failure = new HydrationConsistencyException();
        var mappingSet = ConfigureHydrationFailure(selectedCacheFallback, failure);
        var request = CreateGetRequest(_hydrationFailureDocumentUuid, mappingSet, _schoolResourceInfo);

        var result = await _sut.GetDocumentById(request);

        result
            .Should()
            .BeOfType<GetResult.UnknownFailure>()
            .Which.FailureMessage.Should()
            .Be(failure.Message);
        AssertFailedHydrationWasNotRetriedOrMaterialized();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_maps_query_hydration_exhaustion_to_unknown_failure(bool selectedCacheFallback)
    {
        var failure = new HydrationConsistencyException();
        var mappingSet = ConfigureHydrationFailure(selectedCacheFallback, failure);
        var request = CreateQueryRequest(mappingSet, [], totalCount: false);

        var result = await _sut.QueryDocuments(request);

        result
            .Should()
            .BeOfType<QueryResult.UnknownFailure>()
            .Which.FailureMessage.Should()
            .Be(failure.Message);
        AssertFailedHydrationWasNotRetriedOrMaterialized();
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task It_preserves_other_get_hydration_exceptions(bool selectedCacheFallback, bool canceled)
    {
        Exception failure = canceled
            ? new OperationCanceledException()
            : new InvalidOperationException("Malformed hydration result sets.");
        var mappingSet = ConfigureHydrationFailure(selectedCacheFallback, failure);
        var request = CreateGetRequest(_hydrationFailureDocumentUuid, mappingSet, _schoolResourceInfo);

        Func<Task> act = () => _sut.GetDocumentById(request);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task It_preserves_other_query_hydration_exceptions(bool selectedCacheFallback, bool canceled)
    {
        Exception failure = canceled
            ? new OperationCanceledException()
            : new InvalidOperationException("Malformed hydration result sets.");
        var mappingSet = ConfigureHydrationFailure(selectedCacheFallback, failure);
        var request = CreateQueryRequest(mappingSet, [], totalCount: false);

        Func<Task> act = () => _sut.QueryDocuments(request);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
    }

    private static readonly DocumentUuid _hydrationFailureDocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );

    private MappingSet ConfigureHydrationFailure(bool selectedCacheFallback, Exception failure)
    {
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        if (selectedCacheFallback)
        {
            UseReadAccelerationCoordinator(new RecordingReadAccelerationCoordinator());
        }

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    _hydrationFailureDocumentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    _hydrationFailureDocumentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<DocumentCacheReadAccelerationCandidatePage>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<
                        IRelationalCommandReader,
                        CancellationToken,
                        Task<DocumentCacheReadAccelerationCandidatePage>
                    > readAsync,
                    CancellationToken cancellationToken
                ) =>
                    readAsync(
                        new InMemoryRelationalCommandReader([
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(("DocumentId", 345L))
                            ),
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(
                                    ("DocumentId", 345L),
                                    ("DocumentUuid", _hydrationFailureDocumentUuid.Value),
                                    ("ContentVersion", 91L),
                                    ("ContentLastModifiedAt", _readTargetContentLastModifiedAt)
                                )
                            ),
                        ]),
                        cancellationToken
                    )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => Task.FromException<HydratedPage>(failure));
        return mappingSet;
    }

    private void AssertFailedHydrationWasNotRetriedOrMaterialized()
    {
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
    }
}
