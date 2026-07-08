// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalCommittedRepresentationReader
{
    private const long StampedContentVersion = 91L;

    [Test]
    public async Task It_composes_the_committed_etag_from_the_stamped_content_version()
    {
        var sut = new RelationalCommittedRepresentationReader(
            new ServedEtagComposer(),
            Options.Create(new ResourceLinksOptions())
        );
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        const long documentId = 345L;
        var request = CreateRequest(mappingSet, writePlan, readPlan, documentUuid, documentId);
        var persistedTarget = new RelationalWritePersistResult(
            documentId,
            documentUuid,
            StampedContentVersion
        );

        var result = await sut.ReadAsync(request, persistedTarget);

        // No profile ("_"), links enabled ("l"), JSON ("j"), and the mapping set's schema epoch.
        var expectedEtag = EtagComposer.Compose(
            StampedContentVersion,
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
        long documentId
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
            writePrecondition: new WritePrecondition.None()
        );
}
