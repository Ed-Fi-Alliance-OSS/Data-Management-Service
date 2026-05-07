// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalCommittedRepresentationReader
{
    [Test]
    public async Task It_recomputes_the_committed_response_etag_from_the_profile_projected_surface()
    {
        var sessionDocumentHydrator = A.Fake<ISessionDocumentHydrator>();
        var readMaterializer = A.Fake<IRelationalReadMaterializer>();
        var readableProfileProjector = A.Fake<IReadableProfileProjector>();
        var sut = new RelationalCommittedRepresentationReader(
            sessionDocumentHydrator,
            readMaterializer,
            readableProfileProjector
        );
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        const long documentId = 345L;
        var etagProjectionContext = CreateReadableEtagProjectionContext();
        var request = CreateRequest(
            mappingSet,
            writePlan,
            readPlan,
            documentUuid,
            documentId,
            new WritePrecondition.None(etagProjectionContext)
        );
        var persistedTarget = new RelationalWritePersistResult(documentId, documentUuid);
        var writeSession = CreateWriteSession();
        var hydratedPage = CreateHydratedPage(documentId, documentUuid);
        var materializedResponse = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High",
              "_etag": "\"91\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z"
            }
            """
        )!;
        var projectedResponse = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "schoolId": 255901,
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-11T17:30:45Z"
            }
            """
        )!;
        var expectedProjectedResponse = projectedResponse.DeepClone();
        RelationalApiMetadataFormatter.RefreshEtag(expectedProjectedResponse);

        A.CallTo(() =>
                sessionDocumentHydrator.HydrateAsync(
                    writeSession.Connection,
                    writeSession.Transaction,
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(materializedResponse);
        A.CallTo(() =>
                readableProfileProjector.Project(
                    materializedResponse,
                    etagProjectionContext.ContentTypeDefinition,
                    etagProjectionContext.IdentityPropertyNames
                )
            )
            .Returns(projectedResponse);

        var result = await sut.ReadAsync(request, persistedTarget, writeSession);

        JsonNode
            .DeepEquals(result, expectedProjectedResponse)
            .Should()
            .BeTrue(
                $"""
expected: {expectedProjectedResponse}

actual: {result}
"""
            );
        A.CallTo(() =>
                readableProfileProjector.Project(
                    materializedResponse,
                    etagProjectionContext.ContentTypeDefinition,
                    etagProjectionContext.IdentityPropertyNames
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_returns_the_unprojected_committed_response_when_no_profile_etag_surface_is_present()
    {
        var sessionDocumentHydrator = A.Fake<ISessionDocumentHydrator>();
        var readMaterializer = A.Fake<IRelationalReadMaterializer>();
        var readableProfileProjector = A.Fake<IReadableProfileProjector>();
        var sut = new RelationalCommittedRepresentationReader(
            sessionDocumentHydrator,
            readMaterializer,
            readableProfileProjector
        );
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        const long documentId = 345L;
        var request = CreateRequest(
            mappingSet,
            writePlan,
            readPlan,
            documentUuid,
            documentId,
            new WritePrecondition.None()
        );
        var persistedTarget = new RelationalWritePersistResult(documentId, documentUuid);
        var writeSession = CreateWriteSession();
        var hydratedPage = CreateHydratedPage(documentId, documentUuid);
        var materializedResponse = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "schoolId": 255901,
              "_etag": "\"91\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z"
            }
            """
        )!;

        A.CallTo(() =>
                sessionDocumentHydrator.HydrateAsync(
                    writeSession.Connection,
                    writeSession.Transaction,
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(materializedResponse);

        var result = await sut.ReadAsync(request, persistedTarget, writeSession);

        result.Should().BeSameAs(materializedResponse);
        A.CallTo(() =>
                readableProfileProjector.Project(
                    A<JsonNode>._,
                    A<ContentTypeDefinition>._,
                    A<IReadOnlySet<string>>._
                )
            )
            .MustNotHaveHappened();
    }

    private static ReadableEtagProjectionContext CreateReadableEtagProjectionContext() =>
        new(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("schoolId")],
                [],
                [],
                []
            ),
            new HashSet<string>(["schoolId"], StringComparer.Ordinal)
        );

    private static RelationalWriteExecutorRequest CreateRequest(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        ResourceReadPlan readPlan,
        DocumentUuid documentUuid,
        long documentId,
        WritePrecondition writePrecondition
    ) =>
        new(
            mappingSet,
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetRequest.Put(documentUuid),
            writePlan,
            readPlan,
            JsonNode.Parse("""{"schoolId":255901}""")!,
            false,
            new TraceId("committed-readback-trace"),
            new ReferenceResolverRequest(
                MappingSet: mappingSet,
                RequestResource: writePlan.Model.Resource,
                DocumentReferences: [],
                DescriptorReferences: []
            ),
            new RelationalWriteTargetContext.ExistingDocument(documentId, documentUuid, 44L),
            writePrecondition: writePrecondition
        );

    private static HydratedPage CreateHydratedPage(long documentId, DocumentUuid documentUuid) =>
        new(
            TotalCount: null,
            DocumentMetadata:
            [
                new DocumentMetadataRow(
                    documentId,
                    documentUuid.Value,
                    91L,
                    91L,
                    new DateTimeOffset(2026, 4, 11, 17, 30, 45, TimeSpan.Zero),
                    new DateTimeOffset(2026, 4, 11, 17, 30, 45, TimeSpan.Zero)
                ),
            ],
            TableRowsInDependencyOrder: [],
            DescriptorRowsInPlanOrder: []
        );

    private static IRelationalWriteSession CreateWriteSession()
    {
        var writeSession = A.Fake<IRelationalWriteSession>();
        A.CallTo(() => writeSession.Connection).Returns(A.Fake<DbConnection>());
        A.CallTo(() => writeSession.Transaction).Returns(A.Fake<DbTransaction>());
        return writeSession;
    }
}
