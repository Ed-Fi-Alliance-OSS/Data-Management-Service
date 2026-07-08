// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DescriptorDocumentMaterializer
{
    private static readonly IEtagComposer _etagComposer = new EtagComposer();
    private static readonly VariantKey _descriptorVariantKey = DescriptorVariantKey.For("abcd1234");

    [Test]
    public void It_materializes_external_response_documents_with_public_fields_metadata_and_null_omission()
    {
        var row = CreateDescriptorRow(
            documentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
            contentLastModifiedAt: new DateTimeOffset(2026, 5, 5, 9, 30, 45, 987, TimeSpan.FromHours(-5)),
            description: null,
            effectiveBeginDate: new DateOnly(2025, 1, 15),
            effectiveEndDate: new DateOnly(2025, 12, 31),
            discriminator: "SchoolTypeDescriptor"
        );
        var composedEtag = _etagComposer.Compose(row.ContentVersion, _descriptorVariantKey);

        var result = DescriptorDocumentMaterializer.Materialize(
            row,
            RelationalGetRequestReadMode.ExternalResponse,
            composedEtag
        );

        result["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        result["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        result["shortDescription"]!.GetValue<string>().Should().Be("Alternative");
        result["description"].Should().BeNull();
        result["effectiveBeginDate"]!.GetValue<string>().Should().Be("2025-01-15");
        result["effectiveEndDate"]!.GetValue<string>().Should().Be("2025-12-31");
        result["id"]!.GetValue<string>().Should().Be("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        result["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-05T14:30:45Z");
        result["_etag"]!.GetValue<string>().Should().Be(composedEtag);
        result["Uri"].Should().BeNull();
        result["Discriminator"].Should().BeNull();
        result["ChangeVersion"].Should().BeNull();
    }

    [Test]
    public void It_materializes_stored_documents_without_response_metadata()
    {
        var row = CreateDescriptorRow(
            documentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc"),
            contentLastModifiedAt: new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero),
            description: "Alternative school type",
            effectiveBeginDate: null,
            effectiveEndDate: null,
            discriminator: null
        );

        var result = DescriptorDocumentMaterializer.Materialize(
            row,
            RelationalGetRequestReadMode.StoredDocument,
            composedEtag: null
        );

        result
            .ToJsonString()
            .Should()
            .Be(
                """{"namespace":"uri://ed-fi.org/SchoolTypeDescriptor","codeValue":"Alternative","shortDescription":"Alternative","description":"Alternative school type"}"""
            );
        result["id"].Should().BeNull();
        result["_etag"].Should().BeNull();
        result["_lastModifiedDate"].Should().BeNull();
        result["Uri"].Should().BeNull();
        result["Discriminator"].Should().BeNull();
        result["ChangeVersion"].Should().BeNull();
    }

    [Test]
    public void It_carries_the_callers_precomposed_etag_through_unchanged()
    {
        var row = CreateDescriptorRow(
            documentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-dddddddddddd"),
            contentLastModifiedAt: new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero),
            description: "Alternative school type",
            effectiveBeginDate: new DateOnly(2025, 1, 15),
            effectiveEndDate: null,
            discriminator: "SchoolTypeDescriptor",
            contentVersion: 7L
        );

        // The materializer no longer composes the etag itself: it is the caller's responsibility
        // (DescriptorReadHandler, via IServedEtagComposer) to decide profile-sensitivity and pass
        // the final string through.
        var externalResponse = DescriptorDocumentMaterializer.Materialize(
            row,
            RelationalGetRequestReadMode.ExternalResponse,
            "7-deadbeef.j.somecode.n"
        );

        externalResponse["_etag"]!.GetValue<string>().Should().Be("7-deadbeef.j.somecode.n");
    }

    [Test]
    public void It_throws_when_an_external_response_read_has_no_composed_etag()
    {
        var row = CreateDescriptorRow(
            documentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-eeeeeeeeeeee"),
            contentLastModifiedAt: new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero),
            description: "Alternative school type",
            effectiveBeginDate: null,
            effectiveEndDate: null,
            discriminator: "SchoolTypeDescriptor"
        );

        var act = () =>
            DescriptorDocumentMaterializer.Materialize(
                row,
                RelationalGetRequestReadMode.ExternalResponse,
                composedEtag: null
            );

        act.Should().Throw<InvalidOperationException>();
    }

    private static DescriptorReadRow CreateDescriptorRow(
        Guid documentUuid,
        DateTimeOffset contentLastModifiedAt,
        string? description,
        DateOnly? effectiveBeginDate,
        DateOnly? effectiveEndDate,
        string? discriminator,
        long contentVersion = 42L
    )
    {
        return new DescriptorReadRow(
            DocumentId: 101L,
            DocumentUuid: documentUuid,
            ContentVersion: contentVersion,
            ContentLastModifiedAt: contentLastModifiedAt,
            ResourceKeyId: 13,
            Namespace: "uri://ed-fi.org/SchoolTypeDescriptor",
            CodeValue: "Alternative",
            ShortDescription: "Alternative",
            Description: description,
            EffectiveBeginDate: effectiveBeginDate,
            EffectiveEndDate: effectiveEndDate,
            Discriminator: discriminator
        );
    }
}
