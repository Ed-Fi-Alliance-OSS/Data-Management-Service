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

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

[TestFixture]
[Parallelizable]
public class Given_ResourceEtagFormatter
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

        ResourceEtagFormatter
            .FormatEtag(firstDocument)
            .Should()
            .Be(ResourceEtagFormatter.FormatEtag(secondDocument));
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
        var canonicalDocument = JsonNode.Parse(
            """
            {
              "name": "A&B <tag>"
            }
            """
        )!;

        var expectedHash = Convert.ToBase64String(
            SHA256.HashData(CanonicalJsonSerializer.SerializeToUtf8Bytes(canonicalDocument))
        );
        var legacySerializerHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDocument.ToJsonString()))
        );

        ResourceEtagFormatter.FormatEtag(document).Should().Be(expectedHash);
        ResourceEtagFormatter.FormatEtag(document).Should().NotBe(legacySerializerHash);
    }

    [Test]
    public void It_ignores_server_generated_fields_recursively()
    {
        var documentWithServerFields = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z",
              "studentUniqueId": "10001",
              "schoolReference": {
                "schoolId": 255901,
                "link": {
                  "href": "/ed-fi/schools/bbbbbbbb-1111-2222-3333-cccccccccccc"
                }
              },
              "addresses": [
                {
                  "id": "server-generated-collection-id",
                  "streetNumberName": "100 Main St",
                  "periods": [
                    {
                      "_etag": "nested-stale",
                      "beginDate": "2026-01-01"
                    }
                  ]
                }
              ]
            }
            """
        )!;
        var documentWithoutServerFields = JsonNode.Parse(
            """
            {
              "studentUniqueId": "10001",
              "schoolReference": {
                "schoolId": 255901
              },
              "addresses": [
                {
                  "streetNumberName": "100 Main St",
                  "periods": [
                    {
                      "beginDate": "2026-01-01"
                    }
                  ]
                }
              ]
            }
            """
        )!;

        ResourceEtagFormatter
            .FormatEtag(documentWithServerFields)
            .Should()
            .Be(ResourceEtagFormatter.FormatEtag(documentWithoutServerFields));
    }

    [Test]
    public void It_does_not_mutate_the_document_when_ignoring_server_generated_fields()
    {
        var document = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "_etag": "stale",
              "name": "Lincoln High",
              "schoolReference": {
                "schoolId": 255901,
                "link": {
                  "href": "/ed-fi/schools/bbbbbbbb-1111-2222-3333-cccccccccccc"
                }
              }
            }
            """
        )!;
        var originalJson = document.ToJsonString();

        ResourceEtagFormatter.FormatEtag(document);

        document.ToJsonString().Should().Be(originalJson);
    }

    [Test]
    public void It_formats_a_representative_write_payload_repeatedly_with_the_same_etag()
    {
        var document = JsonNode.Parse(
            """
            {
              "studentUniqueId": "10001",
              "educationOrganizationReference": {
                "educationOrganizationId": 255901
              },
              "addresses": [
                {
                  "streetNumberName": "100 Main St",
                  "city": "Austin",
                  "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                  "periods": [
                    {
                      "beginDate": "2025-08-15",
                      "endDate": "2026-05-29"
                    }
                  ]
                }
              ],
              "electronicMails": [
                {
                  "electronicMailTypeDescriptor": "uri://ed-fi.org/ElectronicMailTypeDescriptor#Home/Personal",
                  "electronicMailAddress": "student@example.test"
                }
              ],
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z"
            }
            """
        )!;
        var expected = ResourceEtagFormatter.FormatEtag(document);

        for (var index = 0; index < 100; index++)
        {
            ResourceEtagFormatter.FormatEtag(document).Should().Be(expected);
        }
    }
}
