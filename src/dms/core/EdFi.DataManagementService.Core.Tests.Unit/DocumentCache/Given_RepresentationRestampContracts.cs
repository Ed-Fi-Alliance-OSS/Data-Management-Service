// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
public class Given_RepresentationRestampContracts
{
    [Test]
    public void It_serializes_the_new_tokens_in_lower_camel_case()
    {
        JsonSerializer.Serialize(DocumentCacheAdministrativeCommand.RepresentationRestamp)
            .Should()
            .Be("\"representationRestamp\"");
        JsonSerializer.Serialize(DocumentCacheAdministrativeCommandConfirmation.RepresentationRestamp)
            .Should()
            .Be("\"representationRestamp\"");
        JsonSerializer.Serialize(DocumentCacheAdministrativeCommandPhase.StampDocuments)
            .Should()
            .Be("\"stampDocuments\"");
        JsonSerializer.Serialize(DocumentCacheRepresentationRestampMode.Tracking).Should().Be("\"tracking\"");
        JsonSerializer.Serialize(DocumentCacheRepresentationRestampOperationState.Incomplete)
            .Should()
            .Be("\"incomplete\"");
        JsonSerializer.Serialize(DocumentCacheRepresentationRestampClaimLevel.ProjectionWorkQueued)
            .Should()
            .Be("\"projectionWorkQueued\"");
        JsonSerializer.Serialize(DocumentCacheAdministrativeCommandPhase.CreateManifest)
            .Should()
            .Be("\"createManifest\"");
        JsonSerializer.Serialize(DocumentCacheAdministrativeCommandPhase.SelectDocuments)
            .Should()
            .Be("\"selectDocuments\"");
    }

    [Test]
    public void It_sorts_uuid_scope_and_rejects_more_than_page_size()
    {
        Guid first = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid second = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var scope = new DocumentCacheRepresentationRestampDocumentUuidsScope([second, first]);

        scope.TryCanonicalize(projectorPageSize: 2, out DocumentCacheRepresentationRestampScope canonical)
            .Should()
            .BeTrue();
        ((DocumentCacheRepresentationRestampDocumentUuidsScope)canonical).DocumentUuids.Should().Equal(first, second);
        new DocumentCacheRepresentationRestampDocumentUuidsScope([first, second, Guid.NewGuid()])
            .TryCanonicalize(2, out _)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_rejects_blank_resource_parts_and_duplicate_uuids()
    {
        new DocumentCacheRepresentationRestampResourceScope(" ", "students")
            .TryCanonicalize(10, out _)
            .Should()
            .BeFalse();
        new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", " ")
            .TryCanonicalize(10, out _)
            .Should()
            .BeFalse();
        Guid uuid = Guid.NewGuid();
        new DocumentCacheRepresentationRestampDocumentUuidsScope([uuid, uuid])
            .TryCanonicalize(10, out _)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_validates_reason_bounds()
    {
        var target = new DocumentCacheAdministrativeTargetKey("tenant", 1);
        var scope = new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "students");
        Action blank = () => new DocumentCacheRepresentationRestampPreviewRequest(
            target,
            null,
            DocumentCacheRepresentationRestampMode.Tracking,
            " ",
            scope
        );
        blank.Should().Throw<ArgumentException>();
        Action longReason = () => new DocumentCacheRepresentationRestampPreviewRequest(
            target,
            null,
            DocumentCacheRepresentationRestampMode.Tracking,
            new string('x', 1025),
            scope
        );
        longReason.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_deserializes_closed_and_drained_as_an_admission_token()
    {
        JsonSerializer.Deserialize<DocumentCacheOfflineWriterAdmission>("\"closedAndDrained\"")!
            .Confirmed.Should().BeTrue();
    }
}
