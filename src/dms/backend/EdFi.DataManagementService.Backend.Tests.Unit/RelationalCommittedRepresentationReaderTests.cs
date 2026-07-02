// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalCommittedRepresentationReader
{
    [Test]
    public async Task It_composes_the_committed_etag_from_content_version_and_variant_key()
    {
        var sessionDocumentHydrator = A.Fake<ISessionDocumentHydrator>();
        var sut = new RelationalCommittedRepresentationReader(
            sessionDocumentHydrator,
            new EtagComposer(),
            Options.Create(new ResourceLinksOptions())
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

        var result = await sut.ReadAsync(request, persistedTarget, writeSession);

        // ContentVersion 91 (from the hydrated metadata), no write profile ("_"), links enabled ("l"),
        // JSON format ("j"), and the mapping set's schema epoch.
        var expectedEtag = new EtagComposer().Compose(
            91L,
            VariantKeyFactory.Create(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileVariantCode.Of(null),
                linksEnabled: true
            )
        );
        result.Should().BeOfType<JsonObject>();
        result["_etag"]!.GetValue<string>().Should().Be(expectedEtag);
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
