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
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z",
              "link": {
                "rel": "self",
                "href": "/ed-fi/schools/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"
              },
              "name": "A&B <tag>",
              "schoolReference": {
                "schoolId": 255901,
                "link": {
                  "rel": "School",
                  "href": "/ed-fi/schools/bbbbbbbb-1111-2222-3333-cccccccccccc"
                }
              }
            }
            """
        )!;
        var canonicalDocument = JsonNode.Parse(
            """
            {
              "name": "A&B <tag>",
              "schoolReference": {
                "schoolId": 255901
              }
            }
            """
        )!;

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
    public void It_ignores_link_subtrees_in_root_reference_collection_nested_collection_and_extensions()
    {
        var documentWithLinks = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z",
              "link": {
                "href": "/ed-fi/students/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"
              },
              "studentUniqueId": "10001",
              "schoolReference": {
                "schoolId": 255901,
                "link": {
                  "href": "/ed-fi/schools/bbbbbbbb-1111-2222-3333-cccccccccccc"
                }
              },
              "addresses": [
                {
                  "id": "collection-item-1",
                  "streetNumberName": "100 Main St",
                  "schoolReference": {
                    "schoolId": 255901,
                    "link": {
                      "href": "/ed-fi/schools/bbbbbbbb-1111-2222-3333-cccccccccccc"
                    }
                  },
                  "periods": [
                    {
                      "id": "nested-item-1",
                      "beginDate": "2026-01-01",
                      "schoolReference": {
                        "schoolId": 255901,
                        "link": {
                          "href": "/ed-fi/schools/bbbbbbbb-1111-2222-3333-cccccccccccc"
                        }
                      }
                    }
                  ]
                }
              ],
              "_ext": {
                "twentyOne": {
                  "cohorts": [
                    {
                      "educationOrganizationReference": {
                        "educationOrganizationId": 255901,
                        "link": {
                          "href": "/ed-fi/educationOrganizations/bbbbbbbb-1111-2222-3333-cccccccccccc"
                        }
                      }
                    }
                  ]
                }
              }
            }
            """
        )!;
        var documentWithoutLinks = JsonNode.Parse(
            """
            {
              "studentUniqueId": "10001",
              "schoolReference": {
                "schoolId": 255901
              },
              "addresses": [
                {
                  "streetNumberName": "100 Main St",
                  "schoolReference": {
                    "schoolId": 255901
                  },
                  "periods": [
                    {
                      "beginDate": "2026-01-01",
                      "schoolReference": {
                        "schoolId": 255901
                      }
                    }
                  ]
                }
              ],
              "_ext": {
                "twentyOne": {
                  "cohorts": [
                    {
                      "educationOrganizationReference": {
                        "educationOrganizationId": 255901
                      }
                    }
                  ]
                }
              }
            }
            """
        )!;

        RelationalApiMetadataFormatter
            .FormatEtag(documentWithLinks)
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(documentWithoutLinks));
    }

    [Test]
    public void It_changes_the_etag_when_a_real_reference_identity_value_changes()
    {
        var firstDocument = JsonNode.Parse(
            """
            {
              "studentUniqueId": "10001",
              "schoolReference": {
                "schoolId": 255901
              }
            }
            """
        )!;
        var secondDocument = JsonNode.Parse(
            """
            {
              "studentUniqueId": "10001",
              "schoolReference": {
                "schoolId": 255902
              }
            }
            """
        )!;

        RelationalApiMetadataFormatter
            .FormatEtag(firstDocument)
            .Should()
            .NotBe(RelationalApiMetadataFormatter.FormatEtag(secondDocument));
    }

    [Test]
    public void It_uses_the_same_canonicalization_path_for_descriptor_bodies()
    {
        var descriptorBody = new ExtractedDescriptorBody(
            "uri://ed-fi.org/SchoolTypeDescriptor",
            "Alternative",
            "Alternative",
            "Alternative school type",
            new DateOnly(2025, 1, 15),
            new DateOnly(2025, 12, 31),
            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
            "SchoolTypeDescriptor"
        );
        var externalResponseDocument = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-13T12:00:00Z",
              "link": {
                "href": "/ed-fi/schoolTypeDescriptors/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"
              },
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Alternative",
              "shortDescription": "Alternative",
              "description": "Alternative school type",
              "effectiveBeginDate": "2025-01-15",
              "effectiveEndDate": "2025-12-31"
            }
            """
        )!;

        RelationalApiMetadataFormatter
            .FormatEtag(descriptorBody)
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(externalResponseDocument));
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
