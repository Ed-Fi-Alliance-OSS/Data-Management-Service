// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Etag;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheMaterializerStreamEtagComposer
{
    [Test]
    public void It_composes_ordinary_resource_stream_etags_with_the_fixed_link_bearing_context()
    {
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var mappingSet = CreateMappingSet();

        var streamEtag = DocumentCacheMaterializerStreamEtagComposer.ComposeForResource(
            servedEtagComposer,
            mappingSet,
            contentVersion: 91
        );

        streamEtag.Should().Be("stream-etag");
        servedEtagComposer
            .CapturedContext.Should()
            .Be(
                new ServedEtagContext(
                    mappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    ProfileName: null,
                    LinksEnabled: true,
                    ContentVersion: 91,
                    ResponseContentCoding.Identity
                )
            );
    }

    [Test]
    public void It_composes_descriptor_stream_etags_with_the_fixed_no_link_context()
    {
        var servedEtagComposer = new RecordingServedEtagComposer("descriptor-stream-etag");
        var mappingSet = CreateMappingSet();

        var streamEtag = DocumentCacheMaterializerStreamEtagComposer.ComposeForDescriptor(
            servedEtagComposer,
            mappingSet,
            contentVersion: 37
        );

        streamEtag.Should().Be("descriptor-stream-etag");
        servedEtagComposer
            .CapturedContext.Should()
            .Be(
                new ServedEtagContext(
                    mappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    ProfileName: null,
                    LinksEnabled: false,
                    ContentVersion: 37,
                    ResponseContentCoding.Identity
                )
            );
    }

    [Test]
    public void It_uses_the_shared_served_etag_composer_output_without_a_local_formatter()
    {
        var mappingSet = CreateMappingSet();
        var servedEtagComposer = new ServedEtagComposer();

        DocumentCacheMaterializerStreamEtagComposer
            .ComposeForResource(servedEtagComposer, mappingSet, contentVersion: 91)
            .Should()
            .Be("91-01234567.j._.l.i");
        DocumentCacheMaterializerStreamEtagComposer
            .ComposeForDescriptor(servedEtagComposer, mappingSet, contentVersion: 91)
            .Should()
            .Be("91-01234567.j._.n.i");
    }

    [Test]
    public void It_keeps_stream_etag_separate_from_cache_document_json()
    {
        var mappingSet = CreateMappingSet();
        var streamEtag = DocumentCacheMaterializerStreamEtagComposer.ComposeForResource(
            new ServedEtagComposer(),
            mappingSet,
            contentVersion: 91
        );

        var candidate = new DocumentCacheMaterializationCandidate(
            documentId: 123,
            documentUuid: new DocumentUuid(Guid.Parse("11111111-1111-2222-3333-444444444444")),
            projectName: "Ed-Fi",
            resourceName: "School",
            resourceVersion: "5.3.0",
            contentVersion: 91,
            lastModifiedAt: DateTimeOffset.Parse("2026-04-03T14:10:11Z", CultureInfo.InvariantCulture),
            streamEtag,
            documentJson: JsonNode
                .Parse(
                    """
                    {"id":"11111111-1111-2222-3333-444444444444","_lastModifiedDate":"2026-04-03T14:10:11Z","nameOfInstitution":"Lincoln High"}
                    """
                )!
                .AsObject()
        );

        candidate.StreamEtag.Should().Be("91-01234567.j._.l.i");
        candidate.DocumentJson.Should().NotContainKey("_etag");
    }

    [Test]
    public void It_does_not_accept_ResourceLinksOptions_as_a_cache_stream_etag_input()
    {
        typeof(DocumentCacheMaterializerStreamEtagComposer)
            .GetMethods()
            .SelectMany(static method => method.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .Should()
            .NotContain(typeof(ResourceLinksOptions));
    }

    private static MappingSet CreateMappingSet() =>
        new(
            Key: new MappingSetKey(
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                SqlDialect.Pgsql,
                "v1"
            ),
            Model: null!,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

    private sealed class RecordingServedEtagComposer(string returnValue) : IServedEtagComposer
    {
        public ServedEtagContext? CapturedContext { get; private set; }

        public string Compose(ServedEtagContext context)
        {
            CapturedContext = context;
            return returnValue;
        }
    }
}
