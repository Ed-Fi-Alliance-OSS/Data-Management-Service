// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalCommittedRepresentationReader
{
    [Test]
    public async Task It_returns_the_full_committed_response_as_materialized()
    {
        var sessionDocumentHydrator = A.Fake<ISessionDocumentHydrator>();
        var readMaterializer = A.Fake<IRelationalReadMaterializer>();
        var sut = new RelationalCommittedRepresentationReader(sessionDocumentHydrator, readMaterializer);
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
              "nameOfInstitution": "Lincoln High",
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
                sessionDocumentHydrator.HydrateAsync(
                    writeSession.Connection,
                    writeSession.Transaction,
                    readPlan,
                    new PageKeysetSpec.Single(documentId),
                    A<HydrationExecutionOptions>.That.Matches(options =>
                        options.IncludeDescriptorProjection
                        && !options.IncludeDocumentReferenceLookup
                        && options.UseSingleDocumentFastPath
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

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
