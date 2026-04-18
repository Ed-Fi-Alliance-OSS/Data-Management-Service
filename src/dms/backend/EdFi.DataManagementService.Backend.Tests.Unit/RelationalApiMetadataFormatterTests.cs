// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalApiMetadataFormatter
{
    [Test]
    public void It_generates_the_same_etag_for_equivalent_documents_with_different_property_order()
    {
        var firstDocument = JsonNode.Parse(
            """
            {
              "b": {
                "z": "last",
                "a": "first"
              },
              "a": "root-first"
            }
            """
        )!;
        var secondDocument = JsonNode.Parse(
            """
            {
              "a": "root-first",
              "b": {
                "a": "first",
                "z": "last"
              }
            }
            """
        )!;

        RelationalApiMetadataFormatter
            .FormatEtag(firstDocument)
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(secondDocument));
    }

    [Test]
    public void It_hashes_the_canonical_json_form_instead_of_the_default_serializer_output()
    {
        var document = JsonNode.Parse(
            """
            {
              "name": "A&B <tag>",
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z"
            }
            """
        )!;
        var canonicalDocument = document.DeepClone().AsObject();

        canonicalDocument.Remove("_etag");
        canonicalDocument.Remove("_lastModifiedDate");
        canonicalDocument.Remove("id");

        var expectedHash = Convert.ToBase64String(
            SHA256.HashData(CanonicalJsonSerializer.SerializeToUtf8Bytes(canonicalDocument))
        );
        var legacySerializerHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDocument.ToJsonString()))
        );

        RelationalApiMetadataFormatter.FormatEtag(document).Should().Be(expectedHash);
        RelationalApiMetadataFormatter.FormatEtag(document).Should().NotBe(legacySerializerHash);
    }

    [Test]
    public void It_refreshes_etag_from_the_projected_document_shape_without_mutating_other_metadata()
    {
        var projectedDocument = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High"
            }
            """
        )!;
        var expectedHash = RelationalApiMetadataFormatter.FormatEtag(projectedDocument);

        RelationalApiMetadataFormatter.RefreshEtag(projectedDocument);

        projectedDocument["id"]!.GetValue<string>().Should().Be("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        projectedDocument["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-04-13T12:00:00Z");
        projectedDocument["_etag"]!.GetValue<string>().Should().Be(expectedHash);
    }
}
