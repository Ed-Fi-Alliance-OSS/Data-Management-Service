// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
[Parallelizable]
public class Given_DocumentComparer
{
    [Test]
    public void It_generates_the_same_hash_for_equivalent_documents_with_different_property_order()
    {
        var firstDocument = JsonNode.Parse(
            """
            {
              "name": "Lincoln High",
              "address": {
                "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                "city": "Austin"
              },
              "grades": [9, 10]
            }
            """
        )!;
        var secondDocument = JsonNode.Parse(
            """
            {
              "grades": [9, 10],
              "address": {
                "city": "Austin",
                "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX"
              },
              "name": "Lincoln High"
            }
            """
        )!;

        DocumentComparer
            .GenerateContentHash(firstDocument)
            .Should()
            .Be(DocumentComparer.GenerateContentHash(secondDocument));
    }

    [Test]
    public void It_hashes_the_canonical_json_form_instead_of_the_default_serializer_output()
    {
        var document = JsonNode.Parse(
            """
            {
              "name": "A&B <tag>",
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z",
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"
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

        DocumentComparer.GenerateContentHash(document).Should().Be(expectedHash);
        DocumentComparer.GenerateContentHash(document).Should().NotBe(legacySerializerHash);
    }
}
