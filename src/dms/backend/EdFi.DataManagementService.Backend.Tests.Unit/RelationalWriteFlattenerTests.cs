// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RelationalWriteFlattener
{
    private static readonly QualifiedResourceName _presenceGateExampleResource = new(
        "Ed-Fi",
        "PresenceGateExample"
    );
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _schoolCategoryDescriptorResource = new(
        "Ed-Fi",
        "SchoolCategoryDescriptor"
    );
    private static readonly QualifiedResourceName _schoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly BaseResourceInfo _schoolResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("School"),
        false
    );
    private static readonly BaseResourceInfo _schoolTypeDescriptorResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("SchoolTypeDescriptor"),
        true
    );
    private static readonly BaseResourceInfo _schoolCategoryDescriptorResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("SchoolCategoryDescriptor"),
        true
    );

    private RelationalWriteFlattener _sut = null!;
    private FlattenerFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteFlattener();
        _fixture = FlattenerFixture.Create();
    }

    [Test]
    public void It_does_not_keep_the_selected_body_index_prepass_type()
    {
        typeof(RelationalWriteFlattener)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Select(static nestedType => nestedType.Name)
            .Should()
            .NotContain("SelectedBodyIndex");
    }

    [Test]
    public void It_emits_root_and_root_extension_rows_in_compiled_binding_order()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "schoolYear": 2026,
                  "details": {
                    "code": "ABC",
                    "active": true,
                    "staffCount": 5000000000,
                    "startDate": "2026-08-19",
                    "lastModified": "2026-08-19T16:30:45Z",
                    "meetingTime": "14:05:07"
                  },
                  "schoolReference": {
                    "schoolId": 255901
                  },
                  "programTypeDescriptor": "uri://ed-fi.org/programtypedescriptor#stem",
                  "_ext": {
                    "sample": {
                      "favoriteColor": "Green"
                    }
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid)
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(2026),
                new FlattenedWriteValue.Literal("ABC"),
                new FlattenedWriteValue.Literal(true),
                new FlattenedWriteValue.Literal(5000000000L),
                new FlattenedWriteValue.Literal(new DateOnly(2026, 8, 19)),
                new FlattenedWriteValue.Literal(new DateTime(2026, 8, 19, 16, 30, 45, DateTimeKind.Utc)),
                new FlattenedWriteValue.Literal(new TimeOnly(14, 5, 7)),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal(77L)
            );

        result.RootRow.RootExtensionRows.Should().ContainSingle();
        result
            .RootRow.RootExtensionRows[0]
            .Values.Should()
            .Equal(new FlattenedWriteValue.Literal(345L), new FlattenedWriteValue.Literal("Green"));
    }

    [Test]
    public void It_emits_root_extension_child_collection_candidates_under_the_root_extension_row()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "_ext": {
                    "sample": {
                      "interventions": [
                        {
                          "interventionCode": "Attendance"
                        },
                        {
                          "interventionCode": "Behavior"
                        }
                      ]
                    }
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.CollectionCandidates.Should().BeEmpty();
        result.RootRow.RootExtensionRows.Should().ContainSingle();

        var extensionRow = result.RootRow.RootExtensionRows[0];
        extensionRow
            .Values.Should()
            .Equal(new FlattenedWriteValue.Literal(345L), new FlattenedWriteValue.Literal(null));

        extensionRow.CollectionCandidates.Should().HaveCount(2);
        extensionRow.CollectionCandidates[0].OrdinalPath.Should().Equal(0);
        extensionRow.CollectionCandidates[0].RequestOrder.Should().Be(0);
        var firstExtensionCollectionItemId = extensionRow
            .CollectionCandidates[0]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        extensionRow.CollectionCandidates[0].Values[1].Should().Be(new FlattenedWriteValue.Literal(345L));
        extensionRow.CollectionCandidates[0].Values[2].Should().Be(new FlattenedWriteValue.Literal(0));
        extensionRow
            .CollectionCandidates[0]
            .Values[3]
            .Should()
            .Be(new FlattenedWriteValue.Literal("Attendance"));
        extensionRow.CollectionCandidates[0].SemanticIdentityValues.Should().Equal("Attendance");

        extensionRow.CollectionCandidates[1].OrdinalPath.Should().Equal(1);
        extensionRow.CollectionCandidates[1].RequestOrder.Should().Be(1);
        var secondExtensionCollectionItemId = extensionRow
            .CollectionCandidates[1]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        secondExtensionCollectionItemId.Token.Should().NotBe(firstExtensionCollectionItemId.Token);
        extensionRow.CollectionCandidates[1].Values[1].Should().Be(new FlattenedWriteValue.Literal(345L));
        extensionRow.CollectionCandidates[1].Values[2].Should().Be(new FlattenedWriteValue.Literal(1));
        extensionRow
            .CollectionCandidates[1]
            .Values[3]
            .Should()
            .Be(new FlattenedWriteValue.Literal("Behavior"));
        extensionRow.CollectionCandidates[1].SemanticIdentityValues.Should().Equal("Behavior");
    }

    [Test]
    public void It_attaches_collection_aligned_extension_scope_rows_to_the_owning_collection_candidate()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "addressLine1": "1 Main St",
                      "_ext": {
                        "sample": {
                          "favoriteColor": "Purple"
                        }
                      }
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);
        var addressCandidate = result.RootRow.CollectionCandidates.Single();

        result.RootRow.RootExtensionRows.Should().BeEmpty();
        addressCandidate.AttachedAlignedScopeData.Should().ContainSingle();
        addressCandidate.CollectionCandidates.Should().BeEmpty();
        var addressCollectionItemId = addressCandidate
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        var alignedScopeCollectionItemId = addressCandidate
            .AttachedAlignedScopeData[0]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        alignedScopeCollectionItemId.Token.Should().Be(addressCollectionItemId.Token);
        addressCandidate
            .AttachedAlignedScopeData[0]
            .Values[1]
            .Should()
            .Be(new FlattenedWriteValue.Literal("Purple"));
        addressCandidate.AttachedAlignedScopeData[0].CollectionCandidates.Should().BeEmpty();
    }

    [Test]
    public void It_emits_collection_aligned_extension_child_collection_candidates_under_the_attached_scope()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "_ext": {
                        "sample": {
                          "services": [
                            {
                              "serviceName": "Bus"
                            },
                            {
                              "serviceName": "Meal"
                            }
                          ]
                        }
                      }
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);
        var addressCandidate = result.RootRow.CollectionCandidates.Single();
        var addressCollectionItemId = addressCandidate
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        var alignedScope = addressCandidate.AttachedAlignedScopeData.Single();
        var alignedScopeCollectionItemId = alignedScope
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        alignedScopeCollectionItemId.Token.Should().Be(addressCollectionItemId.Token);
        alignedScope.Values[1].Should().Be(new FlattenedWriteValue.Literal(null));

        alignedScope.CollectionCandidates.Should().HaveCount(2);
        alignedScope.CollectionCandidates[0].OrdinalPath.Should().Equal(0, 0);
        alignedScope.CollectionCandidates[0].RequestOrder.Should().Be(0);
        var firstAlignedChildCollectionItemId = alignedScope
            .CollectionCandidates[0]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        alignedScope.CollectionCandidates[0].Values[1].Should().Be(new FlattenedWriteValue.Literal(345L));
        alignedScope
            .CollectionCandidates[0]
            .Values[2]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Which.Token.Should()
            .Be(addressCollectionItemId.Token);
        alignedScope.CollectionCandidates[0].Values[3].Should().Be(new FlattenedWriteValue.Literal(0));
        alignedScope.CollectionCandidates[0].Values[4].Should().Be(new FlattenedWriteValue.Literal("Bus"));
        alignedScope.CollectionCandidates[0].SemanticIdentityValues.Should().Equal("Bus");

        alignedScope.CollectionCandidates[1].OrdinalPath.Should().Equal(0, 1);
        alignedScope.CollectionCandidates[1].RequestOrder.Should().Be(1);
        var secondAlignedChildCollectionItemId = alignedScope
            .CollectionCandidates[1]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        secondAlignedChildCollectionItemId.Token.Should().NotBe(firstAlignedChildCollectionItemId.Token);
        alignedScope.CollectionCandidates[1].Values[1].Should().Be(new FlattenedWriteValue.Literal(345L));
        alignedScope
            .CollectionCandidates[1]
            .Values[2]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Which.Token.Should()
            .Be(addressCollectionItemId.Token);
        alignedScope.CollectionCandidates[1].Values[3].Should().Be(new FlattenedWriteValue.Literal(1));
        alignedScope.CollectionCandidates[1].Values[4].Should().Be(new FlattenedWriteValue.Literal("Meal"));
        alignedScope.CollectionCandidates[1].SemanticIdentityValues.Should().Equal("Meal");
    }

    [Test]
    public void It_does_not_emit_collection_aligned_extension_scope_rows_for_empty_extension_sites()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "_ext": {
                        "sample": {
                          "services": []
                        }
                      }
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.CollectionCandidates.Single().AttachedAlignedScopeData.Should().BeEmpty();
    }

    [Test]
    public void It_emits_collection_aligned_extension_scope_row_for_empty_extension_site_when_profile_empty_buffer_flag_is_set()
    {
        // Profile Slice 5: a profile-shaped collection item may carry a VisiblePresent
        // aligned scope with no bound scalar data after writable shaping. The flattener
        // must still attach the aligned scope buffer so separate-scope synthesis can apply
        // the Insert/Update overlay from scope metadata rather than inferring absence from
        // buffer presence.
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "_ext": {
                        "sample": {}
                      }
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences(),
            emitEmptyExtensionBuffers: true
        );

        var result = _sut.Flatten(flatteningInput);
        var addressCandidate = result.RootRow.CollectionCandidates.Single();
        var addressCollectionItemId = addressCandidate
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;

        var alignedScope = addressCandidate.AttachedAlignedScopeData.Should().ContainSingle().Subject;
        var alignedScopeCollectionItemId = alignedScope
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        alignedScopeCollectionItemId.Token.Should().Be(addressCollectionItemId.Token);
        alignedScope.Values[1].Should().Be(new FlattenedWriteValue.Literal(null));
        alignedScope.CollectionCandidates.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_emit_collection_aligned_extension_scope_rows_for_deeply_nested_all_array_extension_sites()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "_ext": {
                        "sample": {
                          "nested": {
                            "onlyArrays": []
                          }
                        }
                      }
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.CollectionCandidates.Single().AttachedAlignedScopeData.Should().BeEmpty();
    }

    [Test]
    public void It_skips_top_level_collection_candidates_when_the_selected_body_contains_an_empty_array()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": []
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.CollectionCandidates.Should().BeEmpty();
        result.RootRow.RootExtensionRows.Should().BeEmpty();
    }

    [Test]
    public void It_emits_top_level_collection_candidates_with_unresolved_keys_and_semantic_identity()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "addressLine1": "1 Main St"
                    },
                    {
                      "addressType": "Work",
                      "addressLine1": "2 State St"
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.CollectionCandidates.Should().HaveCount(2);
        result.RootRow.CollectionCandidates[0].OrdinalPath.Should().Equal(0);
        result.RootRow.CollectionCandidates[0].RequestOrder.Should().Be(0);
        var firstCollectionItemId = result
            .RootRow.CollectionCandidates[0]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        result.RootRow.CollectionCandidates[0].Values[1].Should().Be(new FlattenedWriteValue.Literal(345L));
        result.RootRow.CollectionCandidates[0].Values[2].Should().Be(new FlattenedWriteValue.Literal(0));
        result.RootRow.CollectionCandidates[0].Values[3].Should().Be(new FlattenedWriteValue.Literal("Home"));
        result
            .RootRow.CollectionCandidates[0]
            .Values[4]
            .Should()
            .Be(new FlattenedWriteValue.Literal("1 Main St"));
        result.RootRow.CollectionCandidates[0].SemanticIdentityValues.Should().Equal("Home");

        result.RootRow.CollectionCandidates[1].OrdinalPath.Should().Equal(1);
        result.RootRow.CollectionCandidates[1].RequestOrder.Should().Be(1);
        var secondCollectionItemId = result
            .RootRow.CollectionCandidates[1]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        secondCollectionItemId.Token.Should().NotBe(firstCollectionItemId.Token);
        result.RootRow.CollectionCandidates[1].Values[1].Should().Be(new FlattenedWriteValue.Literal(345L));
        result.RootRow.CollectionCandidates[1].Values[2].Should().Be(new FlattenedWriteValue.Literal(1));
        result.RootRow.CollectionCandidates[1].Values[3].Should().Be(new FlattenedWriteValue.Literal("Work"));
        result
            .RootRow.CollectionCandidates[1]
            .Values[4]
            .Should()
            .Be(new FlattenedWriteValue.Literal("2 State St"));
        result.RootRow.CollectionCandidates[1].SemanticIdentityValues.Should().Equal("Work");
    }

    [Test]
    public void It_emits_nested_collection_candidates_under_the_owning_parent_candidate()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "addressLine1": "1 Main St",
                      "periods": [
                        {
                          "beginDate": "2026-08-20",
                          "schoolReference": {
                            "schoolId": 255901
                          }
                        },
                        {
                          "beginDate": "2027-08-20",
                          "schoolReference": {
                            "schoolId": 255902
                          }
                        }
                      ]
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid)
        );

        var result = _sut.Flatten(flatteningInput);
        var addressCandidate = result.RootRow.CollectionCandidates.Single();
        var addressCollectionItemId = addressCandidate
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        var firstNestedCollectionItemId = addressCandidate
            .CollectionCandidates[0]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        var secondNestedCollectionItemId = addressCandidate
            .CollectionCandidates[1]
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        firstNestedCollectionItemId.Token.Should().NotBe(addressCollectionItemId.Token);
        secondNestedCollectionItemId.Token.Should().NotBe(addressCollectionItemId.Token);
        secondNestedCollectionItemId.Token.Should().NotBe(firstNestedCollectionItemId.Token);

        addressCandidate.CollectionCandidates.Should().HaveCount(2);
        addressCandidate.CollectionCandidates[0].OrdinalPath.Should().Equal(0, 0);
        addressCandidate.CollectionCandidates[0].RequestOrder.Should().Be(0);
        addressCandidate
            .CollectionCandidates[0]
            .SemanticIdentityValues.Should()
            .Equal(new DateOnly(2026, 8, 20));
        addressCandidate.CollectionCandidates[0].Values[0].Should().BeSameAs(firstNestedCollectionItemId);
        addressCandidate
            .CollectionCandidates[0]
            .Values[1]
            .Should()
            .BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        addressCandidate.CollectionCandidates[0].Values[2].Should().BeSameAs(addressCollectionItemId);
        addressCandidate.CollectionCandidates[0].Values[3].Should().Be(new FlattenedWriteValue.Literal(0));
        addressCandidate
            .CollectionCandidates[0]
            .Values[4]
            .Should()
            .Be(new FlattenedWriteValue.Literal(new DateOnly(2026, 8, 20)));
        addressCandidate
            .CollectionCandidates[0]
            .Values[5]
            .Should()
            .Be(new FlattenedWriteValue.Literal(9901L));

        addressCandidate.CollectionCandidates[1].OrdinalPath.Should().Equal(0, 1);
        addressCandidate.CollectionCandidates[1].RequestOrder.Should().Be(1);
        addressCandidate
            .CollectionCandidates[1]
            .SemanticIdentityValues.Should()
            .Equal(new DateOnly(2027, 8, 20));
        addressCandidate.CollectionCandidates[1].Values[0].Should().BeSameAs(secondNestedCollectionItemId);
        addressCandidate
            .CollectionCandidates[1]
            .Values[1]
            .Should()
            .BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        addressCandidate.CollectionCandidates[1].Values[2].Should().BeSameAs(addressCollectionItemId);
        addressCandidate.CollectionCandidates[1].Values[3].Should().Be(new FlattenedWriteValue.Literal(1));
        addressCandidate
            .CollectionCandidates[1]
            .Values[4]
            .Should()
            .Be(new FlattenedWriteValue.Literal(new DateOnly(2027, 8, 20)));
        addressCandidate
            .CollectionCandidates[1]
            .Values[5]
            .Should()
            .Be(new FlattenedWriteValue.Literal(9902L));
    }

    [Test]
    public void It_preserves_request_sibling_order_for_collection_candidates()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Work"
                    },
                    {
                      "addressType": "Home"
                    },
                    {
                      "addressType": "Mailing"
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.CollectionCandidates.Select(candidate =>
                (candidate.RequestOrder, candidate.SemanticIdentityValues[0])
            )
            .Should()
            .Equal((0, (object?)"Work"), (1, (object?)"Home"), (2, (object?)"Mailing"));
    }

    [Test]
    public void It_rejects_duplicate_collection_semantic_identity_values_under_the_same_parent_scope()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home"
                    },
                    {
                      "addressType": "Home"
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.addresses[1]");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain(
                "Collection table 'edfi.StudentAddress' received duplicate semantic identity values ['Home'] under parent scope '$'."
            );
    }

    [Test]
    public void It_rejects_collection_siblings_that_collapse_to_the_same_no_profile_semantic_identity_when_one_omits_and_one_explicit_nulls_the_identity_property()
    {
        // The shared no-profile flattener path matches collection rows by raw semantic identity
        // values (object?[]) with no presence flag. With the no-profile mode flag set, a sibling
        // that omits an identity property and one that sends it as JSON null must be treated as
        // a duplicate at the shared duplicate-detection stage; otherwise both rows would survive
        // flattening and later collide in RelationalWriteNoProfileMerge under the same collapsed
        // key. Profile-aware presence fidelity is covered by the companion profile-mode test
        // below; DMS-1132 owns missing-vs-explicit-null identity semantics for the no-profile
        // path.
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressLine1": "123 Main St"
                    },
                    {
                      "addressType": null,
                      "addressLine1": "456 Oak Ave"
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences(),
            collapseMissingAndExplicitNullForDuplicateDetection: true
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.addresses[1]");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain(
                "Collection table 'edfi.StudentAddress' received duplicate semantic identity values [null] under parent scope '$'."
            );
    }

    [Test]
    public void It_keeps_collection_siblings_distinct_in_profile_mode_when_one_omits_and_one_explicit_nulls_the_identity_property()
    {
        // Counter-test for the no-profile rejection above: with the default profile-mode flag,
        // duplicate detection uses presence-aware SemanticIdentityKeys.BuildKey output so a
        // missing identity property and an explicit JSON null produce distinct keys. The two
        // siblings must therefore both survive flattening and reach the profile merge planner,
        // which pairs them by presence-aware identity. This guards against the no-profile fix
        // collapsing identities universally and rejecting legitimate profile requests where
        // SemanticIdentityPart.IsPresent intentionally distinguishes the two siblings.
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressLine1": "123 Main St"
                    },
                    {
                      "addressType": null,
                      "addressLine1": "456 Oak Ave"
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.CollectionCandidates.Select(candidate =>
                (candidate.RequestOrder, candidate.SemanticIdentityValues[0])
            )
            .Should()
            .Equal((0, (object?)null), (1, (object?)null));
        result
            .RootRow.CollectionCandidates.Select(candidate => candidate.SemanticIdentityInOrder[0].IsPresent)
            .Should()
            .Equal(false, true);
    }

    [Test]
    public void It_treats_the_selected_body_as_authoritative_input()
    {
        var originalBody = JsonNode.Parse(
            """
            {
              "schoolYear": 2026,
              "details": {
                "code": "ORIGINAL"
              },
              "_ext": {
                "sample": {
                  "favoriteColor": "Blue"
                }
              }
            }
            """
        )!;

        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "schoolYear": 2030,
                  "details": {}
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        originalBody["schoolYear"]!.GetValue<int>().Should().Be(2026);
        result.RootRow.Values[0].Should().BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        result.RootRow.Values[1].Should().Be(new FlattenedWriteValue.Literal(2030));
        result.RootRow.Values[2].Should().Be(new FlattenedWriteValue.Literal(null));
        result.RootRow.Values[8].Should().Be(new FlattenedWriteValue.Literal(null));
        result.RootRow.Values[9].Should().Be(new FlattenedWriteValue.Literal(null));
        result.RootRow.RootExtensionRows.Should().BeEmpty();
    }

    [Test]
    public void It_returns_a_validation_failure_when_a_root_document_reference_is_present_but_missing_from_the_resolved_lookup_set()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolId": 255901
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.schoolReference");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain("could not materialize document reference 'Ed-Fi.School' at path '$.schoolReference'");
    }

    [Test]
    public void It_returns_a_validation_failure_when_a_nested_document_reference_is_present_but_missing_from_the_resolved_lookup_set()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "periods": [
                        {
                          "beginDate": "2026-08-20",
                          "schoolReference": {
                            "schoolId": 255901
                          }
                        }
                      ]
                    }
                  ]
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.addresses[0].periods[0].schoolReference");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain(
                "could not materialize document reference 'Ed-Fi.School' at path '$.addresses[0].periods[0].schoolReference'"
            );
    }

    [Test]
    public void It_fails_when_a_descriptor_value_is_present_but_missing_from_the_resolved_lookup_set()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "programTypeDescriptor": "uri://ed-fi.org/programtypedescriptor#stem"
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Column 'ProgramTypeDescriptorId' on table 'edfi.Student' had a descriptor value at path '$.programTypeDescriptor'*resolved lookup set did not contain a matching 'Ed-Fi.ProgramTypeDescriptor' entry for ordinal path []*"
            );
    }

    [Test]
    public void It_does_not_emit_root_extension_rows_for_empty_extension_sites()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "_ext": {
                    "sample": {
                      "interventions": []
                    }
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.RootExtensionRows.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_emit_root_extension_rows_for_deeply_nested_all_array_extension_sites()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "_ext": {
                    "sample": {
                      "nested": {
                        "onlyArrays": []
                      }
                    }
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.RootExtensionRows.Should().BeEmpty();
    }

    [Test]
    public void It_emits_root_extension_row_for_empty_extension_site_when_emit_empty_buffers_flag_is_set()
    {
        // Profile Slice 3: a profile-shaped body may carry a visible scope with no bound data
        // (e.g. _ext: { sample: {} } after the writable request shaper filters its members).
        // The flattener must emit the buffer so the synthesizer's Insert/Update overlay has
        // something to work with instead of treating scope visibility as inferable from
        // buffer presence. Non-profile callers keep the drop-empty default.
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "_ext": {
                    "sample": {}
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences(),
            emitEmptyExtensionBuffers: true
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.RootExtensionRows.Should().ContainSingle();
        result
            .RootRow.RootExtensionRows[0]
            .TableWritePlan.TableModel.JsonScope.Canonical.Should()
            .Be("$._ext.sample");
    }

    [Test]
    public void It_rejects_scalar_values_that_do_not_match_the_compiled_type()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "schoolYear": "2026"
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.schoolYear");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain("Column 'SchoolYear' on table 'edfi.Student' expected scalar kind 'Int32'");
    }

    // Direct flattener tests bypass Core normalization and assert backend-local parsing against the supplied selected body.
    [Test]
    public void It_reads_iso_datetime_offsets_and_iso_time_values_from_the_supplied_selected_body_deterministically()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "details": {
                    "lastModified": "2026-08-19T11:30:45-05:00",
                    "meetingTime": "14:05:07"
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values[6]
            .Should()
            .Be(new FlattenedWriteValue.Literal(new DateTime(2026, 8, 19, 16, 30, 45, DateTimeKind.Utc)));
        result.RootRow.Values[7].Should().Be(new FlattenedWriteValue.Literal(new TimeOnly(14, 5, 7)));
    }

    [Test]
    public void It_rejects_non_iso_datetime_values_from_an_unnormalized_selected_body()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "details": {
                    "lastModified": "2026-08-19 16:30:45"
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.details.lastModified");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain("Column 'LastModified' on table 'edfi.Student' expected scalar kind 'DateTime'");
    }

    [Test]
    public void It_rejects_non_iso_time_values_from_an_unnormalized_selected_body()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "details": {
                    "meetingTime": "2:05 PM"
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.details.meetingTime");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain("Column 'MeetingTime' on table 'edfi.Student' expected scalar kind 'Time'");
    }

    [Test]
    public void It_populates_key_unification_precomputed_values_and_presence_flags()
    {
        var writePlan = CreatePresenceGateExampleWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "presenceGateExampleId": 1001,
                  "secondarySchoolTypeDescriptor": "uri://ed-fi.org/schooltypedescriptor#secondary"
                }
                """
            )!,
            CreateResolvedDescriptorReferences(
                ("$.secondarySchoolTypeDescriptor", 702L, "uri://ed-fi.org/schooltypedescriptor#secondary")
            )
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(456L),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(702L),
                new FlattenedWriteValue.Literal(true),
                new FlattenedWriteValue.Literal(1001)
            );
    }

    [Test]
    public void It_rejects_conflicting_key_unification_descriptor_members()
    {
        var writePlan = CreatePresenceGateExampleWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "presenceGateExampleId": 1001,
                  "primarySchoolTypeDescriptor": "uri://ed-fi.org/schooltypedescriptor#elementary",
                  "secondarySchoolTypeDescriptor": "uri://ed-fi.org/schooltypedescriptor#secondary"
                }
                """
            )!,
            CreateResolvedDescriptorReferences(
                ("$.primarySchoolTypeDescriptor", 701L, "uri://ed-fi.org/schooltypedescriptor#elementary"),
                ("$.secondarySchoolTypeDescriptor", 702L, "uri://ed-fi.org/schooltypedescriptor#secondary")
            )
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.secondarySchoolTypeDescriptor");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain(
                "Key-unification conflict for canonical column 'PrimarySchoolTypeDescriptor_Unified_DescriptorId'"
            );
    }

    [Test]
    public void It_populates_direct_reference_derived_bindings_from_resolved_reference_values()
    {
        var writePlan = CreateReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolId": 1,
                    "schoolYear": 1900
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        ("$.schoolId", "255901"),
                        ("$.schoolYear", "2026")
                    ),
                ]
            )
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(456L),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal(2026),
                new FlattenedWriteValue.Literal(255901)
            );
    }

    [Test]
    public void It_populates_duplicate_scalar_reference_derived_bindings_from_one_logical_reference_path()
    {
        var writePlan = CreateDuplicateScalarReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolCode": "ignored-body-value"
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        ("$.schoolId", "255901"),
                        ("$.localEducationAgencyId", "255902")
                    ),
                ]
            )
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(456L),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal("255901"),
                new FlattenedWriteValue.Literal("255902")
            );
    }

    [Test]
    public void It_does_not_silently_swap_duplicate_reference_derived_columns_when_resolved_identity_order_drifts()
    {
        // The flattening seam guards the old order-only assumption end to end: same-kind identities
        // may arrive in a different resolved order, but propagated columns still have to bind by IdentityJsonPath.
        var writePlan = CreateDuplicateScalarReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolCode": "ignored-body-value"
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        ("$.localEducationAgencyId", "255902"),
                        ("$.schoolId", "255901")
                    ),
                ]
            )
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(456L),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal("255901"),
                new FlattenedWriteValue.Literal("255902")
            );
    }

    [Test]
    public void It_returns_a_validation_failure_when_a_reference_derived_member_is_present_but_missing_from_the_resolved_lookup_set()
    {
        var writePlan = CreateReferenceDerivedValueOnlyWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolId": 1,
                    "schoolYear": 1900
                  }
                }
                """
            )!,
            FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.schoolReference.schoolYear");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain("could not materialize reference-derived value at path '$.schoolReference.schoolYear'")
            .And.Contain("reference object '$.schoolReference'");
    }

    [Test]
    public void It_uses_the_shared_scalar_conversion_error_shape_for_invalid_reference_derived_scalar_values()
    {
        var writePlan = CreateReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolId": 1,
                    "schoolYear": 1900
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        ("$.schoolId", "255901"),
                        ("$.schoolYear", "not-a-number")
                    ),
                ]
            )
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.schoolReference.schoolYear");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain(
                "Column 'School_RefSchoolYear' on table 'edfi.ProgramReferenceDerived' expected scalar kind 'Int32'"
            )
            .And.Contain("path '$.schoolReference.schoolYear'")
            .And.Contain("resolved reference-derived raw value 'not-a-number' could not be converted");
    }

    [Test]
    public void It_uses_the_shared_scalar_conversion_error_shape_for_invalid_nested_reference_derived_scalar_values()
    {
        var writePlan = CreateNestedReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "addresses": [
                    {
                      "addressType": "Home",
                      "periods": [
                        {
                          "beginDate": "2026-08-19",
                          "schoolReference": {
                            "active": true
                          }
                        }
                      ]
                    }
                  ]
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.addresses[0].periods[0].schoolReference",
                        901L,
                        ("$.active", "not-a-bool")
                    ),
                ]
            )
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception
            .ValidationFailures[0]
            .Path.Value.Should()
            .Be("$.addresses[0].periods[0].schoolReference.active");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain(
                "Column 'School_RefIsActive' on table 'edfi.StudentNestedReferenceDerivedPeriod' expected scalar kind 'Boolean'"
            )
            .And.Contain("path '$.addresses[0].periods[0].schoolReference.active'")
            .And.Contain("resolved reference-derived raw value 'not-a-bool' could not be converted");
    }

    [Test]
    public void It_keeps_missing_reference_derived_scalar_type_metadata_as_an_invariant_breach()
    {
        var writePlan = CreateReferenceDerivedWritePlanWithMissingScalarTypeMetadata();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolId": 1,
                    "schoolYear": 1900
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        ("$.schoolId", "255901"),
                        ("$.schoolYear", "1900")
                    ),
                ]
            )
        );

        var act = () => _sut.Flatten(flatteningInput);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Column 'School_RefSchoolYear' on table 'edfi.ProgramReferenceDerived' cannot materialize ReferenceDerived without scalar type metadata.*"
            );
    }

    [Test]
    public void It_uses_the_shared_scalar_conversion_error_shape_for_invalid_key_unification_scalar_members()
    {
        var writePlan = CreateMixedSourceReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "localSchoolId": "not-a-number"
                }
                """
            )!,
            FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        var exception = act.Should().Throw<RelationalWriteRequestValidationException>().Which;

        exception.ValidationFailures.Should().ContainSingle();
        exception.ValidationFailures[0].Path.Value.Should().Be("$.localSchoolId");
        exception
            .ValidationFailures[0]
            .Message.Should()
            .Contain(
                "Key-unification member 'SchoolId_LocalAlias' on table 'edfi.ProgramReferenceDerived' expected scalar kind 'Int32'"
            )
            .And.Contain("path '$.localSchoolId'")
            .And.Contain("encountered JSON value kind 'String' with raw value \"not-a-number\"");
    }

    private static DbColumnModel CreateTestColumn(
        string columnName,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        bool isNullable,
        JsonPathExpression? sourceJsonPath = null,
        QualifiedResourceName? targetResource = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: kind,
            ScalarType: scalarType,
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: targetResource,
            Storage: new ColumnStorage.Stored()
        );
    }

    [Test]
    public void It_populates_descriptor_backed_reference_derived_bindings_on_create()
    {
        var writePlan = CreateDescriptorBackedReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolCategoryDescriptor": "uri://ed-fi.org/schoolcategorydescriptor#body"
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        (
                            DocumentIdentity.DescriptorIdentityJsonPath.Value,
                            "uri://ed-fi.org/schoolcategorydescriptor#resolved"
                        )
                    ),
                ],
                descriptorReferences:
                [
                    CreateResolvedSchoolCategoryDescriptorReference(
                        "$.schoolReference.schoolCategoryDescriptor",
                        501L,
                        "uri://ed-fi.org/schoolcategorydescriptor#resolved"
                    ),
                ]
            )
        );

        var result = _sut.Flatten(flatteningInput);

        result.RootRow.Values[0].Should().BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        result.RootRow.Values[1].Should().Be(new FlattenedWriteValue.Literal(901L));
        result.RootRow.Values[2].Should().Be(new FlattenedWriteValue.Literal(501L));
    }

    [Test]
    public void It_populates_duplicate_scalar_and_descriptor_reference_derived_bindings_from_one_logical_reference_path()
    {
        var writePlan = CreateDuplicateDescriptorReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolCategory": "uri://ed-fi.org/schooltypedescriptor#body"
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        ("$.schoolCategoryCode", "255901"),
                        (
                            DocumentIdentity.DescriptorIdentityJsonPath.Value,
                            "uri://ed-fi.org/schooltypedescriptor#resolved"
                        )
                    ),
                ],
                descriptorReferences:
                [
                    CreateResolvedSchoolTypeDescriptorReference(
                        "$.schoolReference.schoolCategory",
                        501L,
                        "uri://ed-fi.org/schooltypedescriptor#resolved"
                    ),
                ]
            )
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(456L),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal("255901"),
                new FlattenedWriteValue.Literal(501L)
            );
    }

    [Test]
    public void It_populates_reference_derived_key_unification_members_from_resolved_reference_values()
    {
        var writePlan = CreateDescriptorBackedKeyUnificationReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "schoolReference": {
                    "schoolCategoryDescriptor": "uri://ed-fi.org/schoolcategorydescriptor#body"
                  }
                }
                """
            )!,
            CreateResolvedReferenceSet(
                documentReferences:
                [
                    CreateResolvedSchoolReference(
                        "$.schoolReference",
                        901L,
                        (
                            DocumentIdentity.DescriptorIdentityJsonPath.Value,
                            "uri://ed-fi.org/schoolcategorydescriptor#resolved"
                        )
                    ),
                ],
                descriptorReferences:
                [
                    CreateResolvedSchoolCategoryDescriptorReference(
                        "$.schoolReference.schoolCategoryDescriptor",
                        501L,
                        "uri://ed-fi.org/schoolcategorydescriptor#resolved"
                    ),
                ]
            )
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(456L),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal(501L)
            );
    }

    [Test]
    public void It_treats_absent_reference_derived_members_as_semantically_absent_for_key_unification()
    {
        var writePlan = CreateMixedSourceReferenceDerivedWritePlan();
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(456L, _fixture.DocumentUuid),
            writePlan,
            JsonNode.Parse(
                """
                {
                  "localSchoolId": 42
                }
                """
            )!,
            FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(456L),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(42)
            );
    }

    private static ResourceWritePlan CreatePresenceGateExampleWritePlan()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "PresenceGateExample"),
            JsonScope: CreateTestPath("$"),
            Key: new TableKey(
                ConstraintName: "PK_PresenceGateExample",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("PrimarySchoolTypeDescriptor_DescriptorId_Present"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Boolean),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("PrimarySchoolTypeDescriptor_Unified_DescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: descriptorResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SecondarySchoolTypeDescriptor_DescriptorId_Present"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Boolean),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("PresenceGateExampleId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: CreateTestPath(
                        "$.presenceGateExampleId",
                        new JsonPathSegment.Property("presenceGateExampleId")
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var rootPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"PresenceGateExample\" VALUES (...)",
            UpdateSql: "UPDATE edfi.\"PresenceGateExample\" SET ...",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 5, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: tableModel.Columns[0],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[1],
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "primarySchoolTypeDescriptor_DescriptorId_Present"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[2],
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "primarySchoolTypeDescriptor_Unified_DescriptorId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[3],
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "secondarySchoolTypeDescriptor_DescriptorId_Present"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[4],
                    Source: new WriteValueSource.Scalar(
                        CreateTestPath(
                            "$.presenceGateExampleId",
                            new JsonPathSegment.Property("presenceGateExampleId")
                        ),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    ParameterName: "presenceGateExampleId"
                ),
            ],
            KeyUnificationPlans:
            [
                new KeyUnificationWritePlan(
                    CanonicalColumn: new DbColumnName("PrimarySchoolTypeDescriptor_Unified_DescriptorId"),
                    CanonicalBindingIndex: 2,
                    MembersInOrder:
                    [
                        new KeyUnificationMemberWritePlan.DescriptorMember(
                            MemberPathColumn: new DbColumnName("PrimarySchoolTypeDescriptor_DescriptorId"),
                            RelativePath: CreateTestPath(
                                "$.primarySchoolTypeDescriptor",
                                new JsonPathSegment.Property("primarySchoolTypeDescriptor")
                            ),
                            DescriptorResource: descriptorResource,
                            PresenceColumn: new DbColumnName(
                                "PrimarySchoolTypeDescriptor_DescriptorId_Present"
                            ),
                            PresenceBindingIndex: 1,
                            PresenceIsSynthetic: true
                        ),
                        new KeyUnificationMemberWritePlan.DescriptorMember(
                            MemberPathColumn: new DbColumnName("SecondarySchoolTypeDescriptor_DescriptorId"),
                            RelativePath: CreateTestPath(
                                "$.secondarySchoolTypeDescriptor",
                                new JsonPathSegment.Property("secondarySchoolTypeDescriptor")
                            ),
                            DescriptorResource: descriptorResource,
                            PresenceColumn: new DbColumnName(
                                "SecondarySchoolTypeDescriptor_DescriptorId_Present"
                            ),
                            PresenceBindingIndex: 3,
                            PresenceIsSynthetic: true
                        ),
                    ]
                ),
            ]
        );
        var resourceModel = new RelationalResourceModel(
            Resource: _presenceGateExampleResource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tableModel,
            TablesInDependencyOrder: [tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceWritePlan(resourceModel, [rootPlan]);
    }

    private static ResourceWritePlan CreateReferenceDerivedWritePlan()
    {
        return CreateReferenceDerivedWritePlan(CreateReferenceDerivedModel());
    }

    private static ResourceWritePlan CreateReferenceDerivedValueOnlyWritePlan()
    {
        var model = CreateReferenceDerivedModel();
        var table = model.Root;

        DbColumnModel Column(string name)
        {
            return table.Columns.Single(column =>
                string.Equals(column.ColumnName.Value, name, StringComparison.Ordinal)
            );
        }

        return new ResourceWritePlan(
            Model: model,
            TablePlansInDependencyOrder:
            [
                new TableWritePlan(
                    TableModel: table,
                    InsertSql: "INSERT INTO [edfi].[ProgramReferenceDerived] ([DocumentId], [School_RefSchoolYear]) VALUES (@documentId, @schoolRefSchoolYear);",
                    UpdateSql: null,
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(
                        MaxRowsPerBatch: 525,
                        ParametersPerRow: 2,
                        MaxParametersPerCommand: 2100
                    ),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            Column: Column("DocumentId"),
                            Source: new WriteValueSource.DocumentId(),
                            ParameterName: "documentId"
                        ),
                        new WriteColumnBinding(
                            Column: Column("School_RefSchoolYear"),
                            Source: new WriteValueSource.ReferenceDerived(
                                ReferenceSource: new ReferenceDerivedValueSourceMetadata(
                                    BindingIndex: 0,
                                    ReferenceObjectPath: CreateTestPath(
                                        "$.schoolReference",
                                        new JsonPathSegment.Property("schoolReference")
                                    ),
                                    IdentityJsonPath: CreateTestPath(
                                        "$.schoolYear",
                                        new JsonPathSegment.Property("schoolYear")
                                    ),
                                    ReferenceJsonPath: CreateTestPath(
                                        "$.schoolReference.schoolYear",
                                        new JsonPathSegment.Property("schoolReference"),
                                        new JsonPathSegment.Property("schoolYear")
                                    )
                                )
                            ),
                            ParameterName: "schoolRefSchoolYear"
                        ),
                    ],
                    KeyUnificationPlans: []
                ),
            ]
        );
    }

    private static ResourceWritePlan CreateMixedSourceReferenceDerivedWritePlan()
    {
        return CreateReferenceDerivedWritePlan(CreateMixedSourceReferenceDerivedModel());
    }

    private static ResourceWritePlan CreateDuplicateScalarReferenceDerivedWritePlan()
    {
        var referencePath = CreateTestPath(
            "$.schoolReference",
            new JsonPathSegment.Property("schoolReference")
        );
        var duplicatePath = CreateTestPath(
            "$.schoolReference.schoolCode",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolCode")
        );
        var primaryIdentityPath = CreateTestPath("$.schoolId", new JsonPathSegment.Property("schoolId"));
        var secondaryIdentityPath = CreateTestPath(
            "$.localEducationAgencyId",
            new JsonPathSegment.Property("localEducationAgencyId")
        );
        var table = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "ProgramDuplicateScalarReferenceDerived"),
            JsonScope: CreateTestPath("$"),
            Key: new TableKey(
                ConstraintName: "PK_ProgramDuplicateScalarReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: referencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("PrimarySchoolCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: duplicatePath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SecondarySchoolCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: duplicatePath,
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "Program"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: table,
                TablesInDependencyOrder: [table],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: referencePath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: _schoolResource,
                        IdentityBindings:
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: primaryIdentityPath,
                                ReferenceJsonPath: duplicatePath,
                                Column: new DbColumnName("PrimarySchoolCode")
                            ),
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: secondaryIdentityPath,
                                ReferenceJsonPath: duplicatePath,
                                Column: new DbColumnName("SecondarySchoolCode")
                            ),
                        ]
                    ),
                ],
                DescriptorEdgeSources: []
            ),
            [
                new TableWritePlan(
                    TableModel: table,
                    InsertSql: "INSERT INTO [edfi].[ProgramDuplicateScalarReferenceDerived] ([DocumentId], [School_DocumentId], [PrimarySchoolCode], [SecondarySchoolCode]) VALUES (@documentId, @schoolDocumentId, @primarySchoolCode, @secondarySchoolCode);",
                    UpdateSql: null,
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(525, 4, 2100),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            Column: table.Columns[0],
                            Source: new WriteValueSource.DocumentId(),
                            ParameterName: "documentId"
                        ),
                        new WriteColumnBinding(
                            Column: table.Columns[1],
                            Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                            ParameterName: "schoolDocumentId"
                        ),
                        new WriteColumnBinding(
                            Column: table.Columns[2],
                            Source: new WriteValueSource.ReferenceDerived(
                                new ReferenceDerivedValueSourceMetadata(
                                    BindingIndex: 0,
                                    ReferenceObjectPath: referencePath,
                                    IdentityJsonPath: primaryIdentityPath,
                                    ReferenceJsonPath: duplicatePath
                                )
                            ),
                            ParameterName: "primarySchoolCode"
                        ),
                        new WriteColumnBinding(
                            Column: table.Columns[3],
                            Source: new WriteValueSource.ReferenceDerived(
                                new ReferenceDerivedValueSourceMetadata(
                                    BindingIndex: 0,
                                    ReferenceObjectPath: referencePath,
                                    IdentityJsonPath: secondaryIdentityPath,
                                    ReferenceJsonPath: duplicatePath
                                )
                            ),
                            ParameterName: "secondarySchoolCode"
                        ),
                    ],
                    KeyUnificationPlans: []
                ),
            ]
        );
    }

    private static ResourceWritePlan CreateReferenceDerivedWritePlanWithMissingScalarTypeMetadata()
    {
        var model = CreateReferenceDerivedModel();
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns.Select(column =>
                    string.Equals(column.ColumnName.Value, "School_RefSchoolYear", StringComparison.Ordinal)
                        ? column with
                        {
                            ScalarType = null,
                        }
                        : column
                ),
            ],
        };

        return CreateReferenceDerivedWritePlan(
            model with
            {
                Root = rootTable,
                TablesInDependencyOrder = [rootTable],
            }
        );
    }

    private static ResourceWritePlan CreateNestedReferenceDerivedWritePlan()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var rootPlan = CreateNestedReferenceDerivedRootPlan();
        var addressPlan = CreateNestedReferenceDerivedAddressPlan();
        var periodPlan = CreateNestedReferenceDerivedPeriodPlan();
        var referenceObjectPath = CreateTestPath(
            "$.addresses[*].periods[*].schoolReference",
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("periods"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("schoolReference")
        );
        var referenceValuePath = CreateTestPath(
            "$.addresses[*].periods[*].schoolReference.active",
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("periods"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("active")
        );
        var identityPath = CreateTestPath("$.active", new JsonPathSegment.Property("active"));

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: resource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel, addressPlan.TableModel, periodPlan.TableModel],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: referenceObjectPath,
                        Table: periodPlan.TableModel.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: _schoolResource,
                        IdentityBindings:
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: identityPath,
                                ReferenceJsonPath: referenceValuePath,
                                Column: new DbColumnName("School_RefIsActive")
                            ),
                        ]
                    ),
                ],
                DescriptorEdgeSources: []
            ),
            [rootPlan, addressPlan, periodPlan]
        );
    }

    private static ResourceWritePlan CreateReferenceDerivedWritePlan(RelationalResourceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var table = model.Root;

        DbColumnModel Column(string name)
        {
            return table.Columns.Single(column =>
                string.Equals(column.ColumnName.Value, name, StringComparison.Ordinal)
            );
        }

        var keyClass = table.KeyUnificationClasses.Single();
        var keyMembers = keyClass
            .MemberPathColumns.Select(memberPathColumn =>
                (KeyUnificationMemberWritePlan)(
                    memberPathColumn.Value switch
                    {
                        "School_RefSchoolIdAlias" => new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
                            MemberPathColumn: memberPathColumn,
                            RelativePath: CreateTestPath(
                                "$.schoolReference.schoolId",
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("schoolId")
                            ),
                            ReferenceSource: new ReferenceDerivedValueSourceMetadata(
                                BindingIndex: 0,
                                ReferenceObjectPath: CreateTestPath(
                                    "$.schoolReference",
                                    new JsonPathSegment.Property("schoolReference")
                                ),
                                IdentityJsonPath: CreateTestPath(
                                    "$.schoolId",
                                    new JsonPathSegment.Property("schoolId")
                                ),
                                ReferenceJsonPath: CreateTestPath(
                                    "$.schoolReference.schoolId",
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolId")
                                )
                            ),
                            PresenceColumn: new DbColumnName("School_DocumentId"),
                            PresenceBindingIndex: 1,
                            PresenceIsSynthetic: false
                        ),
                        "SchoolId_LocalAlias" => new KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: memberPathColumn,
                            RelativePath: CreateTestPath(
                                "$.localSchoolId",
                                new JsonPathSegment.Property("localSchoolId")
                            ),
                            ScalarType: new RelationalScalarType(ScalarKind.Int32),
                            PresenceColumn: null,
                            PresenceBindingIndex: null,
                            PresenceIsSynthetic: false
                        ),
                        _ => throw new InvalidOperationException(
                            $"Unsupported key-unification member path column '{memberPathColumn.Value}' in reference-derived test fixture."
                        ),
                    }
                )
            )
            .ToArray();

        return new ResourceWritePlan(
            Model: model,
            TablePlansInDependencyOrder:
            [
                new TableWritePlan(
                    TableModel: table,
                    InsertSql: $"INSERT INTO [edfi].[{table.Table.Name}] ([DocumentId], [School_DocumentId], [School_RefSchoolYear], [SchoolId_Canonical]) VALUES (@documentId, @schoolDocumentId, @schoolRefSchoolYear, @schoolIdCanonical);",
                    UpdateSql: null,
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(
                        MaxRowsPerBatch: 525,
                        ParametersPerRow: 4,
                        MaxParametersPerCommand: 2100
                    ),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            Column: Column("DocumentId"),
                            Source: new WriteValueSource.DocumentId(),
                            ParameterName: "documentId"
                        ),
                        new WriteColumnBinding(
                            Column: Column("School_DocumentId"),
                            Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                            ParameterName: "schoolDocumentId"
                        ),
                        new WriteColumnBinding(
                            Column: Column("School_RefSchoolYear"),
                            Source: new WriteValueSource.ReferenceDerived(
                                ReferenceSource: new ReferenceDerivedValueSourceMetadata(
                                    BindingIndex: 0,
                                    ReferenceObjectPath: CreateTestPath(
                                        "$.schoolReference",
                                        new JsonPathSegment.Property("schoolReference")
                                    ),
                                    IdentityJsonPath: CreateTestPath(
                                        "$.schoolYear",
                                        new JsonPathSegment.Property("schoolYear")
                                    ),
                                    ReferenceJsonPath: CreateTestPath(
                                        "$.schoolReference.schoolYear",
                                        new JsonPathSegment.Property("schoolReference"),
                                        new JsonPathSegment.Property("schoolYear")
                                    )
                                )
                            ),
                            ParameterName: "schoolRefSchoolYear"
                        ),
                        new WriteColumnBinding(
                            Column: Column("SchoolId_Canonical"),
                            Source: new WriteValueSource.Precomputed(),
                            ParameterName: "schoolIdCanonical"
                        ),
                    ],
                    KeyUnificationPlans:
                    [
                        new KeyUnificationWritePlan(
                            CanonicalColumn: keyClass.CanonicalColumn,
                            CanonicalBindingIndex: 3,
                            MembersInOrder: keyMembers
                        ),
                    ]
                ),
            ]
        );
    }

    private static TableWritePlan CreateNestedReferenceDerivedRootPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentNestedReferenceDerived"),
            JsonScope: CreateTestPath("$"),
            Key: new TableKey(
                ConstraintName: "PK_StudentNestedReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO [edfi].[StudentNestedReferenceDerived] ([DocumentId]) VALUES (@documentId);",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 1, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: tableModel.Columns[0],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateNestedReferenceDerivedAddressPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentNestedReferenceDerivedAddress"),
            JsonScope: CreateTestPath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentNestedReferenceDerivedAddress",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateTestColumn("CollectionItemId", ColumnKind.CollectionKey, null, isNullable: false),
                CreateTestColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                CreateTestColumn("Ordinal", ColumnKind.Ordinal, null, isNullable: false),
                CreateTestColumn(
                    "AddressType",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    isNullable: false,
                    sourceJsonPath: CreateTestPath(
                        "$.addressType",
                        new JsonPathSegment.Property("addressType")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        CreateTestPath("$.addressType", new JsonPathSegment.Property("addressType")),
                        new DbColumnName("AddressType")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO [edfi].[StudentNestedReferenceDerivedAddress] ([CollectionItemId], [Student_DocumentId], [Ordinal], [AddressType]) VALUES (@collectionItemId, @studentDocumentId, @ordinal, @addressType);",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: tableModel.Columns[0],
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "collectionItemId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[1],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "studentDocumentId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[2],
                    Source: new WriteValueSource.Ordinal(),
                    ParameterName: "ordinal"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[3],
                    Source: new WriteValueSource.Scalar(
                        CreateTestPath("$.addressType", new JsonPathSegment.Property("addressType")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    ParameterName: "addressType"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        CreateTestPath("$.addressType", new JsonPathSegment.Property("addressType")),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE [edfi].[StudentNestedReferenceDerivedAddress] SET [AddressType] = @addressType WHERE [CollectionItemId] = @collectionItemId;",
                DeleteByStableRowIdentitySql: "DELETE FROM [edfi].[StudentNestedReferenceDerivedAddress] WHERE [CollectionItemId] = @collectionItemId;",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateNestedReferenceDerivedPeriodPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentNestedReferenceDerivedPeriod"),
            JsonScope: CreateTestPath(
                "$.addresses[*].periods[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("periods"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentNestedReferenceDerivedPeriod",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateTestColumn("CollectionItemId", ColumnKind.CollectionKey, null, isNullable: false),
                CreateTestColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                CreateTestColumn(
                    "Address_CollectionItemId",
                    ColumnKind.ParentKeyPart,
                    null,
                    isNullable: false
                ),
                CreateTestColumn("Ordinal", ColumnKind.Ordinal, null, isNullable: false),
                CreateTestColumn(
                    "BeginDate",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    isNullable: false,
                    sourceJsonPath: CreateTestPath("$.beginDate", new JsonPathSegment.Property("beginDate"))
                ),
                CreateTestColumn(
                    "School_DocumentId",
                    ColumnKind.DocumentFk,
                    null,
                    isNullable: true,
                    targetResource: _schoolResource
                ),
                CreateTestColumn(
                    "School_RefIsActive",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    isNullable: true,
                    sourceJsonPath: CreateTestPath(
                        "$.addresses[*].periods[*].schoolReference.active",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("periods"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("active")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("Address_CollectionItemId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        CreateTestPath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                        new DbColumnName("BeginDate")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO [edfi].[StudentNestedReferenceDerivedPeriod] ([CollectionItemId], [Student_DocumentId], [Address_CollectionItemId], [Ordinal], [BeginDate], [School_DocumentId], [School_RefIsActive]) VALUES (@collectionItemId, @studentDocumentId, @addressCollectionItemId, @ordinal, @beginDate, @schoolDocumentId, @schoolRefIsActive);",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 7, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: tableModel.Columns[0],
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "collectionItemId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[1],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "studentDocumentId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[2],
                    Source: new WriteValueSource.ParentKeyPart(0),
                    ParameterName: "addressCollectionItemId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[3],
                    Source: new WriteValueSource.Ordinal(),
                    ParameterName: "ordinal"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[4],
                    Source: new WriteValueSource.Scalar(
                        CreateTestPath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                        new RelationalScalarType(ScalarKind.Date)
                    ),
                    ParameterName: "beginDate"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[5],
                    Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                    ParameterName: "schoolDocumentId"
                ),
                new WriteColumnBinding(
                    Column: tableModel.Columns[6],
                    Source: new WriteValueSource.ReferenceDerived(
                        ReferenceSource: new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: CreateTestPath(
                                "$.addresses[*].periods[*].schoolReference",
                                new JsonPathSegment.Property("addresses"),
                                new JsonPathSegment.AnyArrayElement(),
                                new JsonPathSegment.Property("periods"),
                                new JsonPathSegment.AnyArrayElement(),
                                new JsonPathSegment.Property("schoolReference")
                            ),
                            IdentityJsonPath: CreateTestPath(
                                "$.active",
                                new JsonPathSegment.Property("active")
                            ),
                            ReferenceJsonPath: CreateTestPath(
                                "$.addresses[*].periods[*].schoolReference.active",
                                new JsonPathSegment.Property("addresses"),
                                new JsonPathSegment.AnyArrayElement(),
                                new JsonPathSegment.Property("periods"),
                                new JsonPathSegment.AnyArrayElement(),
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("active")
                            )
                        )
                    ),
                    ParameterName: "schoolRefIsActive"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        CreateTestPath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                        4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE [edfi].[StudentNestedReferenceDerivedPeriod] SET [BeginDate] = @beginDate WHERE [CollectionItemId] = @collectionItemId;",
                DeleteByStableRowIdentitySql: "DELETE FROM [edfi].[StudentNestedReferenceDerivedPeriod] WHERE [CollectionItemId] = @collectionItemId;",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [4, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static RelationalResourceModel CreateReferenceDerivedModel()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Program");
        var schoolReferencePath = CreateTestPath(
            "$.schoolReference",
            new JsonPathSegment.Property("schoolReference")
        );
        var schoolIdPath = CreateTestPath(
            "$.schoolReference.schoolId",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolId")
        );
        var schoolIdIdentityPath = CreateTestPath("$.schoolId", new JsonPathSegment.Property("schoolId"));
        var schoolYearPath = CreateTestPath(
            "$.schoolReference.schoolYear",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolYear")
        );
        var schoolYearIdentityPath = CreateTestPath(
            "$.schoolYear",
            new JsonPathSegment.Property("schoolYear")
        );
        var table = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "ProgramReferenceDerived"),
            JsonScope: CreateTestPath("$"),
            Key: new TableKey(
                ConstraintName: "PK_ProgramReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: schoolYearPath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolId_Canonical"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolIdAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: schoolIdPath,
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                        PresenceColumn: new DbColumnName("School_DocumentId")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                    MemberPathColumns: [new DbColumnName("School_RefSchoolIdAlias")]
                ),
            ],
        };

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: table,
            TablesInDependencyOrder: [table],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: table.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: schoolIdIdentityPath,
                            ReferenceJsonPath: schoolIdPath,
                            Column: new DbColumnName("School_RefSchoolIdAlias")
                        ),
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: schoolYearIdentityPath,
                            ReferenceJsonPath: schoolYearPath,
                            Column: new DbColumnName("School_RefSchoolYear")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateMixedSourceReferenceDerivedModel()
    {
        var model = CreateReferenceDerivedModel();
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolId_LocalAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: CreateTestPath(
                        "$.localSchoolId",
                        new JsonPathSegment.Property("localSchoolId")
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                        PresenceColumn: null
                    )
                ),
            ],
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                    MemberPathColumns:
                    [
                        new DbColumnName("School_RefSchoolIdAlias"),
                        new DbColumnName("SchoolId_LocalAlias"),
                    ]
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static ResourceWritePlan CreateDescriptorBackedReferenceDerivedWritePlan()
    {
        var schoolReferencePath = CreateTestPath(
            "$.schoolReference",
            new JsonPathSegment.Property("schoolReference")
        );
        var descriptorPath = CreateTestPath(
            "$.schoolReference.schoolCategoryDescriptor",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolCategoryDescriptor")
        );
        var descriptorIdentityPath = CreateTestPath(
            "$.schoolCategoryDescriptor",
            new JsonPathSegment.Property("schoolCategoryDescriptor")
        );
        var table = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "ProgramDescriptorReferenceDerived"),
            JsonScope: CreateTestPath("$"),
            Key: new TableKey(
                ConstraintName: "PK_ProgramDescriptorReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: descriptorPath,
                    TargetResource: _schoolCategoryDescriptorResource
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var writePlan = new TableWritePlan(
            TableModel: table,
            InsertSql: "INSERT INTO [edfi].[ProgramDescriptorReferenceDerived] ([DocumentId], [School_DocumentId], [SchoolCategoryDescriptorId]) VALUES (@documentId, @schoolDocumentId, @schoolCategoryDescriptorId);",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(700, 3, 2100),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: table.Columns[0],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
                new WriteColumnBinding(
                    Column: table.Columns[1],
                    Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                    ParameterName: "schoolDocumentId"
                ),
                new WriteColumnBinding(
                    Column: table.Columns[2],
                    Source: new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: schoolReferencePath,
                            IdentityJsonPath: descriptorIdentityPath,
                            ReferenceJsonPath: descriptorPath
                        )
                    ),
                    ParameterName: "schoolCategoryDescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "Program"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: table,
                TablesInDependencyOrder: [table],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: schoolReferencePath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: _schoolResource,
                        IdentityBindings:
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: descriptorIdentityPath,
                                ReferenceJsonPath: descriptorPath,
                                Column: new DbColumnName("SchoolCategoryDescriptorId")
                            ),
                        ]
                    ),
                ],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: descriptorPath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("SchoolCategoryDescriptorId"),
                        DescriptorResource: _schoolCategoryDescriptorResource
                    ),
                ]
            ),
            [writePlan]
        );
    }

    private static ResourceWritePlan CreateDuplicateDescriptorReferenceDerivedWritePlan()
    {
        var schoolReferencePath = CreateTestPath(
            "$.schoolReference",
            new JsonPathSegment.Property("schoolReference")
        );
        var duplicatePath = CreateTestPath(
            "$.schoolReference.schoolCategory",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolCategory")
        );
        var schoolCategoryCodeIdentityPath = CreateTestPath(
            "$.schoolCategoryCode",
            new JsonPathSegment.Property("schoolCategoryCode")
        );
        var schoolCategoryDescriptorIdentityPath = CreateTestPath(
            "$.schoolCategoryDescriptor",
            new JsonPathSegment.Property("schoolCategoryDescriptor")
        );
        var table = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "ProgramDuplicateDescriptorReferenceDerived"),
            JsonScope: CreateTestPath("$"),
            Key: new TableKey(
                ConstraintName: "PK_ProgramDuplicateDescriptorReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: duplicatePath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: duplicatePath,
                    TargetResource: _schoolTypeDescriptorResource
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var writePlan = new TableWritePlan(
            TableModel: table,
            InsertSql: "INSERT INTO [edfi].[ProgramDuplicateDescriptorReferenceDerived] ([DocumentId], [School_DocumentId], [SchoolCategoryCode], [SchoolCategoryDescriptorId]) VALUES (@documentId, @schoolDocumentId, @schoolCategoryCode, @schoolCategoryDescriptorId);",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(525, 4, 2100),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: table.Columns[0],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
                new WriteColumnBinding(
                    Column: table.Columns[1],
                    Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                    ParameterName: "schoolDocumentId"
                ),
                new WriteColumnBinding(
                    Column: table.Columns[2],
                    Source: new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: schoolReferencePath,
                            IdentityJsonPath: schoolCategoryCodeIdentityPath,
                            ReferenceJsonPath: duplicatePath
                        )
                    ),
                    ParameterName: "schoolCategoryCode"
                ),
                new WriteColumnBinding(
                    Column: table.Columns[3],
                    Source: new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: schoolReferencePath,
                            IdentityJsonPath: schoolCategoryDescriptorIdentityPath,
                            ReferenceJsonPath: duplicatePath
                        )
                    ),
                    ParameterName: "schoolCategoryDescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "Program"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: table,
                TablesInDependencyOrder: [table],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: schoolReferencePath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: _schoolResource,
                        IdentityBindings:
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: schoolCategoryCodeIdentityPath,
                                ReferenceJsonPath: duplicatePath,
                                Column: new DbColumnName("SchoolCategoryCode")
                            ),
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: schoolCategoryDescriptorIdentityPath,
                                ReferenceJsonPath: duplicatePath,
                                Column: new DbColumnName("SchoolCategoryDescriptorId")
                            ),
                        ]
                    ),
                ],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: duplicatePath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("SchoolCategoryDescriptorId"),
                        DescriptorResource: _schoolTypeDescriptorResource
                    ),
                ]
            ),
            [writePlan]
        );
    }

    private static ResourceWritePlan CreateDescriptorBackedKeyUnificationReferenceDerivedWritePlan()
    {
        var schoolReferencePath = CreateTestPath(
            "$.schoolReference",
            new JsonPathSegment.Property("schoolReference")
        );
        var descriptorPath = CreateTestPath(
            "$.schoolReference.schoolCategoryDescriptor",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolCategoryDescriptor")
        );
        var descriptorIdentityPath = CreateTestPath(
            "$.schoolCategoryDescriptor",
            new JsonPathSegment.Property("schoolCategoryDescriptor")
        );
        var table = new DbTableModel(
            Table: new DbTableName(
                new DbSchemaName("edfi"),
                "ProgramDescriptorKeyUnificationReferenceDerived"
            ),
            JsonScope: CreateTestPath("$"),
            Key: new TableKey(
                ConstraintName: "PK_ProgramDescriptorKeyUnificationReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryDescriptorId_Canonical"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: _schoolCategoryDescriptorResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryDescriptorId_Alias"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: descriptorPath,
                    TargetResource: _schoolCategoryDescriptorResource,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolCategoryDescriptorId_Canonical"),
                        PresenceColumn: new DbColumnName("School_DocumentId")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolCategoryDescriptorId_Canonical"),
                    MemberPathColumns: [new DbColumnName("SchoolCategoryDescriptorId_Alias")]
                ),
            ],
        };

        var writePlan = new TableWritePlan(
            TableModel: table,
            InsertSql: "INSERT INTO [edfi].[ProgramDescriptorKeyUnificationReferenceDerived] ([DocumentId], [School_DocumentId], [SchoolCategoryDescriptorId_Canonical]) VALUES (@documentId, @schoolDocumentId, @schoolCategoryDescriptorIdCanonical);",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(700, 3, 2100),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: table.Columns[0],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
                new WriteColumnBinding(
                    Column: table.Columns[1],
                    Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                    ParameterName: "schoolDocumentId"
                ),
                new WriteColumnBinding(
                    Column: table.Columns[2],
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "schoolCategoryDescriptorIdCanonical"
                ),
            ],
            KeyUnificationPlans:
            [
                new KeyUnificationWritePlan(
                    CanonicalColumn: new DbColumnName("SchoolCategoryDescriptorId_Canonical"),
                    CanonicalBindingIndex: 2,
                    MembersInOrder:
                    [
                        new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
                            MemberPathColumn: new DbColumnName("SchoolCategoryDescriptorId_Alias"),
                            RelativePath: descriptorPath,
                            ReferenceSource: new ReferenceDerivedValueSourceMetadata(
                                BindingIndex: 0,
                                ReferenceObjectPath: schoolReferencePath,
                                IdentityJsonPath: descriptorIdentityPath,
                                ReferenceJsonPath: descriptorPath
                            ),
                            PresenceColumn: new DbColumnName("School_DocumentId"),
                            PresenceBindingIndex: 1,
                            PresenceIsSynthetic: false
                        ),
                    ]
                ),
            ]
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "Program"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: table,
                TablesInDependencyOrder: [table],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: schoolReferencePath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: _schoolResource,
                        IdentityBindings:
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: descriptorIdentityPath,
                                ReferenceJsonPath: descriptorPath,
                                Column: new DbColumnName("SchoolCategoryDescriptorId_Alias")
                            ),
                        ]
                    ),
                ],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: descriptorPath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("SchoolCategoryDescriptorId_Alias"),
                        DescriptorResource: _schoolCategoryDescriptorResource
                    ),
                ]
            ),
            [writePlan]
        );
    }

    private static ResolvedReferenceSet CreateResolvedReferenceSet(
        IEnumerable<ResolvedDocumentReference>? documentReferences = null,
        IEnumerable<ResolvedDescriptorReference>? descriptorReferences = null
    )
    {
        var resolvedDocumentReferences = documentReferences?.ToArray() ?? [];
        var resolvedDescriptorReferences = descriptorReferences?.ToArray() ?? [];

        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: resolvedDocumentReferences.ToDictionary(
                documentReference => documentReference.Reference.Path,
                documentReference => documentReference
            ),
            SuccessfulDescriptorReferencesByPath: resolvedDescriptorReferences.ToDictionary(
                descriptorReference => descriptorReference.Reference.Path,
                descriptorReference => descriptorReference
            ),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static ResolvedDocumentReference CreateResolvedSchoolReference(
        string path,
        long documentId,
        params (string IdentityJsonPath, string IdentityValue)[] identityElements
    )
    {
        return new ResolvedDocumentReference(
            new DocumentReference(
                _schoolResourceInfo,
                new DocumentIdentity([
                    .. identityElements.Select(identityElement => new DocumentIdentityElement(
                        new JsonPath(identityElement.IdentityJsonPath),
                        identityElement.IdentityValue
                    )),
                ]),
                new ReferentialId(Guid.NewGuid()),
                new JsonPath(path)
            ),
            documentId,
            ResourceKeyId: 21
        );
    }

    private static ResolvedDescriptorReference CreateResolvedSchoolCategoryDescriptorReference(
        string path,
        long documentId,
        string uri
    )
    {
        return new ResolvedDescriptorReference(
            new DescriptorReference(
                _schoolCategoryDescriptorResourceInfo,
                new DocumentIdentity([
                    new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri),
                ]),
                new ReferentialId(Guid.NewGuid()),
                new JsonPath(path)
            ),
            documentId,
            ResourceKeyId: 31
        );
    }

    private static ResolvedDescriptorReference CreateResolvedSchoolTypeDescriptorReference(
        string path,
        long documentId,
        string uri
    )
    {
        return new ResolvedDescriptorReference(
            new DescriptorReference(
                _schoolTypeDescriptorResourceInfo,
                new DocumentIdentity([
                    new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri),
                ]),
                new ReferentialId(Guid.NewGuid()),
                new JsonPath(path)
            ),
            documentId,
            ResourceKeyId: 31
        );
    }

    private static ResolvedReferenceSet CreateResolvedDescriptorReferences(
        params (string Path, long DocumentId, string Uri)[] descriptors
    )
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: descriptors.ToDictionary(
                descriptor => new JsonPath(descriptor.Path),
                descriptor => new ResolvedDescriptorReference(
                    new DescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        new DocumentIdentity([
                            new DocumentIdentityElement(
                                DocumentIdentity.DescriptorIdentityJsonPath,
                                descriptor.Uri
                            ),
                        ]),
                        new ReferentialId(Guid.NewGuid()),
                        new JsonPath(descriptor.Path)
                    ),
                    descriptor.DocumentId,
                    ResourceKeyId: 31
                )
            ),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static JsonPathExpression CreateTestPath(string canonical, params JsonPathSegment[] segments)
    {
        return new JsonPathExpression(canonical, segments);
    }

    private sealed record FlattenerFixture(
        DocumentUuid DocumentUuid,
        ResourceWritePlan WritePlan,
        ResolvedReferenceSet ResolvedReferences
    )
    {
        public FlatteningInput CreateFlatteningInput(
            JsonNode selectedBody,
            RelationalWriteTargetContext targetContext,
            ResolvedReferenceSet? resolvedReferences = null,
            bool emitEmptyExtensionBuffers = false,
            bool collapseMissingAndExplicitNullForDuplicateDetection = false
        )
        {
            return new FlatteningInput(
                RelationalWriteOperationKind.Post,
                targetContext,
                WritePlan,
                selectedBody,
                resolvedReferences ?? ResolvedReferences,
                emitEmptyExtensionBuffers,
                collapseMissingAndExplicitNullForDuplicateDetection
            );
        }

        public static FlattenerFixture Create()
        {
            var schoolResource = new QualifiedResourceName("Ed-Fi", "Student");
            var rootPlan = CreateRootPlan();
            var rootExtensionPlan = CreateRootExtensionPlan();
            var rootExtensionInterventionPlan = CreateRootExtensionInterventionPlan();
            var addressPlan = CreateAddressPlan();
            var addressExtensionPlan = CreateAddressExtensionPlan();
            var addressExtensionServicePlan = CreateAddressExtensionServicePlan();
            var addressPeriodPlan = CreateAddressPeriodPlan();

            var resourceModel = new RelationalResourceModel(
                Resource: schoolResource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    rootExtensionPlan.TableModel,
                    rootExtensionInterventionPlan.TableModel,
                    addressPlan.TableModel,
                    addressExtensionPlan.TableModel,
                    addressExtensionServicePlan.TableModel,
                    addressPeriodPlan.TableModel,
                ],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: CreatePath(
                            "$.schoolReference",
                            new JsonPathSegment.Property("schoolReference")
                        ),
                        Table: rootPlan.TableModel.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                        IdentityBindings: []
                    ),
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: CreatePath(
                            "$.addresses[*].periods[*].schoolReference",
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("periods"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("schoolReference")
                        ),
                        Table: addressPeriodPlan.TableModel.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                        IdentityBindings: []
                    ),
                ],
                DescriptorEdgeSources: []
            );

            return new FlattenerFixture(
                DocumentUuid: new DocumentUuid(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
                WritePlan: new ResourceWritePlan(
                    resourceModel,
                    [
                        rootPlan,
                        rootExtensionPlan,
                        rootExtensionInterventionPlan,
                        addressPlan,
                        addressExtensionPlan,
                        addressExtensionServicePlan,
                        addressPeriodPlan,
                    ]
                ),
                ResolvedReferences: CreateResolvedReferences()
            );
        }

        private static ResolvedReferenceSet CreateResolvedReferences()
        {
            var schoolReference = new DocumentReference(
                new BaseResourceInfo(
                    new ProjectName("Ed-Fi"),
                    new ResourceName("School"),
                    IsDescriptor: false
                ),
                new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                new ReferentialId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                new JsonPath("$.schoolReference")
            );
            var programDescriptorReference = new DescriptorReference(
                new BaseResourceInfo(
                    new ProjectName("Ed-Fi"),
                    new ResourceName("ProgramTypeDescriptor"),
                    IsDescriptor: true
                ),
                new DocumentIdentity([
                    new DocumentIdentityElement(
                        DocumentIdentity.DescriptorIdentityJsonPath,
                        "uri://ed-fi.org/programtypedescriptor#stem"
                    ),
                ]),
                new ReferentialId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                new JsonPath("$.programTypeDescriptor")
            );

            return new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
                {
                    [new JsonPath("$.schoolReference")] = new ResolvedDocumentReference(
                        schoolReference,
                        DocumentId: 901L,
                        ResourceKeyId: 21
                    ),
                    [new JsonPath("$.addresses[0].periods[0].schoolReference")] =
                        new ResolvedDocumentReference(
                            new DocumentReference(
                                schoolReference.ResourceInfo,
                                schoolReference.DocumentIdentity,
                                new ReferentialId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                                new JsonPath("$.addresses[0].periods[0].schoolReference")
                            ),
                            DocumentId: 9901L,
                            ResourceKeyId: 21
                        ),
                    [new JsonPath("$.addresses[0].periods[1].schoolReference")] =
                        new ResolvedDocumentReference(
                            new DocumentReference(
                                schoolReference.ResourceInfo,
                                schoolReference.DocumentIdentity,
                                new ReferentialId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
                                new JsonPath("$.addresses[0].periods[1].schoolReference")
                            ),
                            DocumentId: 9902L,
                            ResourceKeyId: 21
                        ),
                },
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
                {
                    [new JsonPath("$.programTypeDescriptor")] = new ResolvedDescriptorReference(
                        programDescriptorReference,
                        DocumentId: 77L,
                        ResourceKeyId: 31
                    ),
                },
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            );
        }

        public static ResolvedReferenceSet CreateEmptyResolvedReferences()
        {
            return new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            );
        }

        private static TableWritePlan CreateRootPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
                JsonScope: CreatePath("$"),
                Key: new TableKey(
                    "PK_Student",
                    [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                ),
                Columns:
                [
                    CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn(
                        "SchoolYear",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Int32),
                        isNullable: true,
                        sourceJsonPath: CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear"))
                    ),
                    CreateColumn(
                        "Code",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.code",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("code")
                        )
                    ),
                    CreateColumn(
                        "IsActive",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Boolean),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.active",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("active")
                        )
                    ),
                    CreateColumn(
                        "StaffCount",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Int64),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.staffCount",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("staffCount")
                        )
                    ),
                    CreateColumn(
                        "StartDate",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Date),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.startDate",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("startDate")
                        )
                    ),
                    CreateColumn(
                        "LastModified",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.DateTime),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.lastModified",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("lastModified")
                        )
                    ),
                    CreateColumn(
                        "MeetingTime",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Time),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.meetingTime",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("meetingTime")
                        )
                    ),
                    CreateColumn(
                        "School_DocumentId",
                        ColumnKind.DocumentFk,
                        null,
                        isNullable: true,
                        targetResource: new QualifiedResourceName("Ed-Fi", "School")
                    ),
                    CreateColumn(
                        "ProgramTypeDescriptorId",
                        ColumnKind.DescriptorFk,
                        null,
                        isNullable: true,
                        targetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.Root,
                    PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                    RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                    ImmediateParentScopeLocatorColumns: [],
                    SemanticIdentityBindings: []
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into edfi.\"Student\" values (...)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.DocumentId(),
                        "DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.Scalar(
                            CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear")),
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                        "SchoolYear"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[2],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.code",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("code")
                            ),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 20)
                        ),
                        "Code"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[3],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.active",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("active")
                            ),
                            new RelationalScalarType(ScalarKind.Boolean)
                        ),
                        "IsActive"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[4],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.staffCount",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("staffCount")
                            ),
                            new RelationalScalarType(ScalarKind.Int64)
                        ),
                        "StaffCount"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[5],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.startDate",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("startDate")
                            ),
                            new RelationalScalarType(ScalarKind.Date)
                        ),
                        "StartDate"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[6],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.lastModified",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("lastModified")
                            ),
                            new RelationalScalarType(ScalarKind.DateTime)
                        ),
                        "LastModified"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[7],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.meetingTime",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("meetingTime")
                            ),
                            new RelationalScalarType(ScalarKind.Time)
                        ),
                        "MeetingTime"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[8],
                        new WriteValueSource.DocumentReference(0),
                        "School_DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[9],
                        new WriteValueSource.DescriptorReference(
                            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
                            CreatePath(
                                "$.programTypeDescriptor",
                                new JsonPathSegment.Property("programTypeDescriptor")
                            ),
                            CreatePath(
                                "$.programTypeDescriptor",
                                new JsonPathSegment.Property("programTypeDescriptor")
                            )
                        ),
                        "ProgramTypeDescriptorId"
                    ),
                ],
                KeyUnificationPlans: []
            );
        }

        private static TableWritePlan CreateRootExtensionPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
                JsonScope: CreatePath(
                    "$._ext.sample",
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample")
                ),
                Key: new TableKey(
                    "PK_StudentExtension",
                    [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                ),
                Columns:
                [
                    CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn(
                        "FavoriteColor",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$._ext.sample.favoriteColor",
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("favoriteColor")
                        )
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.RootExtension,
                    PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                    RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                    ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                    SemanticIdentityBindings: []
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into sample.\"StudentExtension\" values (...)",
                UpdateSql: "update sample.\"StudentExtension\" set ...",
                DeleteByParentSql: "delete from sample.\"StudentExtension\" where ...",
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.ParentKeyPart(0),
                        "DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.Scalar(
                            CreatePath("$.favoriteColor", new JsonPathSegment.Property("favoriteColor")),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                        ),
                        "FavoriteColor"
                    ),
                ],
                KeyUnificationPlans: []
            );
        }

        private static TableWritePlan CreateRootExtensionInterventionPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("sample"), "StudentExtensionIntervention"),
                JsonScope: CreatePath(
                    "$._ext.sample.interventions[*]",
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample"),
                    new JsonPathSegment.Property("interventions"),
                    new JsonPathSegment.AnyArrayElement()
                ),
                Key: new TableKey(
                    "PK_StudentExtensionIntervention",
                    [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
                ),
                Columns:
                [
                    CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, isNullable: false),
                    CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn("Ordinal", ColumnKind.Ordinal, null, isNullable: false),
                    CreateColumn(
                        "InterventionCode",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        isNullable: false,
                        sourceJsonPath: CreatePath(
                            "$.interventionCode",
                            new JsonPathSegment.Property("interventionCode")
                        )
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.ExtensionCollection,
                    PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                    RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                    ImmediateParentScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                    SemanticIdentityBindings:
                    [
                        new CollectionSemanticIdentityBinding(
                            CreatePath(
                                "$.interventionCode",
                                new JsonPathSegment.Property("interventionCode")
                            ),
                            new DbColumnName("InterventionCode")
                        ),
                    ]
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into sample.\"StudentExtensionIntervention\" values (...)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.Precomputed(),
                        "CollectionItemId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.DocumentId(),
                        "Student_DocumentId"
                    ),
                    new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                    new WriteColumnBinding(
                        tableModel.Columns[3],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.interventionCode",
                                new JsonPathSegment.Property("interventionCode")
                            ),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                        ),
                        "InterventionCode"
                    ),
                ],
                KeyUnificationPlans: [],
                CollectionMergePlan: new CollectionMergePlan(
                    SemanticIdentityBindings:
                    [
                        new CollectionMergeSemanticIdentityBinding(
                            CreatePath(
                                "$.interventionCode",
                                new JsonPathSegment.Property("interventionCode")
                            ),
                            3
                        ),
                    ],
                    StableRowIdentityBindingIndex: 0,
                    UpdateByStableRowIdentitySql: "update sample.\"StudentExtensionIntervention\" set \"InterventionCode\" = @InterventionCode where \"CollectionItemId\" = @CollectionItemId",
                    DeleteByStableRowIdentitySql: "delete from sample.\"StudentExtensionIntervention\" where \"CollectionItemId\" = @CollectionItemId",
                    OrdinalBindingIndex: 2,
                    CompareBindingIndexesInOrder: [3, 2]
                ),
                CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                    new DbColumnName("CollectionItemId"),
                    0
                )
            );
        }

        private static TableWritePlan CreateAddressPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
                JsonScope: CreatePath(
                    "$.addresses[*]",
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement()
                ),
                Key: new TableKey(
                    "PK_StudentAddress",
                    [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
                ),
                Columns:
                [
                    CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, isNullable: false),
                    CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn("Ordinal", ColumnKind.Ordinal, null, isNullable: false),
                    CreateColumn(
                        "AddressType",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        isNullable: false,
                        sourceJsonPath: CreatePath(
                            "$.addressType",
                            new JsonPathSegment.Property("addressType")
                        )
                    ),
                    CreateColumn(
                        "AddressLine1",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.addressLine1",
                            new JsonPathSegment.Property("addressLine1")
                        )
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.Collection,
                    PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                    RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                    ImmediateParentScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                    SemanticIdentityBindings:
                    [
                        new CollectionSemanticIdentityBinding(
                            CreatePath("$.addressType", new JsonPathSegment.Property("addressType")),
                            new DbColumnName("AddressType")
                        ),
                    ]
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into edfi.\"StudentAddress\" values (@CollectionItemId, @Student_DocumentId, @Ordinal, @AddressType, @AddressLine1)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.Precomputed(),
                        "CollectionItemId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.DocumentId(),
                        "Student_DocumentId"
                    ),
                    new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                    new WriteColumnBinding(
                        tableModel.Columns[3],
                        new WriteValueSource.Scalar(
                            CreatePath("$.addressType", new JsonPathSegment.Property("addressType")),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                        ),
                        "AddressType"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[4],
                        new WriteValueSource.Scalar(
                            CreatePath("$.addressLine1", new JsonPathSegment.Property("addressLine1")),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                        ),
                        "AddressLine1"
                    ),
                ],
                KeyUnificationPlans: [],
                CollectionMergePlan: new CollectionMergePlan(
                    SemanticIdentityBindings:
                    [
                        new CollectionMergeSemanticIdentityBinding(
                            CreatePath("$.addressType", new JsonPathSegment.Property("addressType")),
                            3
                        ),
                    ],
                    StableRowIdentityBindingIndex: 0,
                    UpdateByStableRowIdentitySql: "update edfi.\"StudentAddress\" set \"AddressType\" = @AddressType where \"CollectionItemId\" = @CollectionItemId",
                    DeleteByStableRowIdentitySql: "delete from edfi.\"StudentAddress\" where \"CollectionItemId\" = @CollectionItemId",
                    OrdinalBindingIndex: 2,
                    CompareBindingIndexesInOrder: [3, 2]
                ),
                CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                    new DbColumnName("CollectionItemId"),
                    0
                )
            );
        }

        private static TableWritePlan CreateAddressPeriodPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressPeriod"),
                JsonScope: CreatePath(
                    "$.addresses[*].periods[*]",
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("periods"),
                    new JsonPathSegment.AnyArrayElement()
                ),
                Key: new TableKey(
                    "PK_StudentAddressPeriod",
                    [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
                ),
                Columns:
                [
                    CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, isNullable: false),
                    CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn(
                        "Address_CollectionItemId",
                        ColumnKind.ParentKeyPart,
                        null,
                        isNullable: false
                    ),
                    CreateColumn("Ordinal", ColumnKind.Ordinal, null, isNullable: false),
                    CreateColumn(
                        "BeginDate",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Date),
                        isNullable: false,
                        sourceJsonPath: CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate"))
                    ),
                    CreateColumn(
                        "School_DocumentId",
                        ColumnKind.DocumentFk,
                        null,
                        isNullable: true,
                        targetResource: new QualifiedResourceName("Ed-Fi", "School")
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.Collection,
                    PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                    RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                    ImmediateParentScopeLocatorColumns: [new DbColumnName("Address_CollectionItemId")],
                    SemanticIdentityBindings:
                    [
                        new CollectionSemanticIdentityBinding(
                            CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                            new DbColumnName("BeginDate")
                        ),
                    ]
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into edfi.\"StudentAddressPeriod\" values (@CollectionItemId, @Student_DocumentId, @Address_CollectionItemId, @Ordinal, @BeginDate, @School_DocumentId)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.Precomputed(),
                        "CollectionItemId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.DocumentId(),
                        "Student_DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[2],
                        new WriteValueSource.ParentKeyPart(0),
                        "Address_CollectionItemId"
                    ),
                    new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                    new WriteColumnBinding(
                        tableModel.Columns[4],
                        new WriteValueSource.Scalar(
                            CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                            new RelationalScalarType(ScalarKind.Date)
                        ),
                        "BeginDate"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[5],
                        new WriteValueSource.DocumentReference(1),
                        "School_DocumentId"
                    ),
                ],
                KeyUnificationPlans: [],
                CollectionMergePlan: new CollectionMergePlan(
                    SemanticIdentityBindings:
                    [
                        new CollectionMergeSemanticIdentityBinding(
                            CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                            4
                        ),
                    ],
                    StableRowIdentityBindingIndex: 0,
                    UpdateByStableRowIdentitySql: "update edfi.\"StudentAddressPeriod\" set \"BeginDate\" = @BeginDate where \"CollectionItemId\" = @CollectionItemId",
                    DeleteByStableRowIdentitySql: "delete from edfi.\"StudentAddressPeriod\" where \"CollectionItemId\" = @CollectionItemId",
                    OrdinalBindingIndex: 3,
                    CompareBindingIndexesInOrder: [4, 3]
                ),
                CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                    new DbColumnName("CollectionItemId"),
                    0
                )
            );
        }

        private static TableWritePlan CreateAddressExtensionPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("sample"), "StudentExtensionAddress"),
                JsonScope: CreatePath(
                    "$.addresses[*]._ext.sample",
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample")
                ),
                Key: new TableKey(
                    "PK_StudentExtensionAddress",
                    [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
                ),
                Columns:
                [
                    CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn(
                        "FavoriteColor",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.favoriteColor",
                            new JsonPathSegment.Property("favoriteColor")
                        )
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.CollectionExtensionScope,
                    PhysicalRowIdentityColumns: [new DbColumnName("BaseCollectionItemId")],
                    RootScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                    ImmediateParentScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                    SemanticIdentityBindings: []
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into sample.\"StudentExtensionAddress\" values (@BaseCollectionItemId, @FavoriteColor)",
                UpdateSql: "update sample.\"StudentExtensionAddress\" set \"FavoriteColor\" = @FavoriteColor where \"BaseCollectionItemId\" = @BaseCollectionItemId",
                DeleteByParentSql: "delete from sample.\"StudentExtensionAddress\" where \"BaseCollectionItemId\" = @BaseCollectionItemId",
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.ParentKeyPart(0),
                        "BaseCollectionItemId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.Scalar(
                            CreatePath("$.favoriteColor", new JsonPathSegment.Property("favoriteColor")),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                        ),
                        "FavoriteColor"
                    ),
                ],
                KeyUnificationPlans: []
            );
        }

        private static TableWritePlan CreateAddressExtensionServicePlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("sample"), "StudentExtensionAddressService"),
                JsonScope: CreatePath(
                    "$.addresses[*]._ext.sample.services[*]",
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample"),
                    new JsonPathSegment.Property("services"),
                    new JsonPathSegment.AnyArrayElement()
                ),
                Key: new TableKey(
                    "PK_StudentExtensionAddressService",
                    [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
                ),
                Columns:
                [
                    CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, isNullable: false),
                    CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn("Ordinal", ColumnKind.Ordinal, null, isNullable: false),
                    CreateColumn(
                        "ServiceName",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        isNullable: false,
                        sourceJsonPath: CreatePath(
                            "$.serviceName",
                            new JsonPathSegment.Property("serviceName")
                        )
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.ExtensionCollection,
                    PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                    RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                    ImmediateParentScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                    SemanticIdentityBindings:
                    [
                        new CollectionSemanticIdentityBinding(
                            CreatePath("$.serviceName", new JsonPathSegment.Property("serviceName")),
                            new DbColumnName("ServiceName")
                        ),
                    ]
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into sample.\"StudentExtensionAddressService\" values (@CollectionItemId, @Student_DocumentId, @BaseCollectionItemId, @Ordinal, @ServiceName)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.Precomputed(),
                        "CollectionItemId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.DocumentId(),
                        "Student_DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[2],
                        new WriteValueSource.ParentKeyPart(0),
                        "BaseCollectionItemId"
                    ),
                    new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                    new WriteColumnBinding(
                        tableModel.Columns[4],
                        new WriteValueSource.Scalar(
                            CreatePath("$.serviceName", new JsonPathSegment.Property("serviceName")),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                        ),
                        "ServiceName"
                    ),
                ],
                KeyUnificationPlans: [],
                CollectionMergePlan: new CollectionMergePlan(
                    SemanticIdentityBindings:
                    [
                        new CollectionMergeSemanticIdentityBinding(
                            CreatePath("$.serviceName", new JsonPathSegment.Property("serviceName")),
                            4
                        ),
                    ],
                    StableRowIdentityBindingIndex: 0,
                    UpdateByStableRowIdentitySql: "update sample.\"StudentExtensionAddressService\" set \"ServiceName\" = @ServiceName where \"CollectionItemId\" = @CollectionItemId",
                    DeleteByStableRowIdentitySql: "delete from sample.\"StudentExtensionAddressService\" where \"CollectionItemId\" = @CollectionItemId",
                    OrdinalBindingIndex: 3,
                    CompareBindingIndexesInOrder: [4, 3]
                ),
                CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                    new DbColumnName("CollectionItemId"),
                    0
                )
            );
        }

        private static DbColumnModel CreateColumn(
            string columnName,
            ColumnKind kind,
            RelationalScalarType? scalarType,
            bool isNullable,
            JsonPathExpression? sourceJsonPath = null,
            QualifiedResourceName? targetResource = null
        )
        {
            return new DbColumnModel(
                ColumnName: new DbColumnName(columnName),
                Kind: kind,
                ScalarType: scalarType,
                IsNullable: isNullable,
                SourceJsonPath: sourceJsonPath,
                TargetResource: targetResource,
                Storage: new ColumnStorage.Stored()
            );
        }

        private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments)
        {
            return new JsonPathExpression(canonical, segments);
        }
    }
}

/// <summary>
/// Regression for the inlined-parent-collection traversal gap: when a collection table's
/// JsonScope is <c>$.parents[*].detail.children[*]</c>, the immediate JSON parent
/// (<c>$.parents[*].detail</c>) is an inlined non-collection scope with no backing table
/// plan. The flattener must still reach the children table by routing the child plan
/// through the nearest table-backed ancestor (<c>$.parents[*]</c>) so each parent
/// candidate carries its child collection candidates with the parent's PhysicalRowIdentity
/// stamped into the parent-key slot.
/// </summary>
[TestFixture]
public class Given_RelationalWriteFlattener_with_an_inlined_parent_collection
{
    private const long DocumentId = 345L;
    private RelationalWriteFlattener _sut = null!;
    private ResourceWritePlan _writePlan = null!;
    private DocumentUuid _documentUuid;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteFlattener();
        var (plan, _, _) = NestedTopologyBuilders.BuildRootParentsAndDetailChildrenPlan();
        _writePlan = plan;
        _documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    }

    [Test]
    public void It_emits_inlined_parent_nested_collection_candidates_under_the_owning_parent_candidate()
    {
        var body = JsonNode.Parse(
            """
            {
              "parents": [
                {
                  "identityField0": "A",
                  "detail": {
                    "children": [
                      { "identityField0": "A1" },
                      { "identityField0": "A2" }
                    ]
                  }
                },
                {
                  "identityField0": "B",
                  "detail": {
                    "children": [
                      { "identityField0": "B1" }
                    ]
                  }
                }
              ]
            }
            """
        )!;

        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(DocumentId, _documentUuid),
            _writePlan,
            body,
            EmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        var parentCandidates = result.RootRow.CollectionCandidates;
        parentCandidates.Should().HaveCount(2, "the request body has two top-level parent rows");

        var parentACandidate = parentCandidates[0];
        parentACandidate.SemanticIdentityValues.Should().Equal("A");
        var parentACollectionItemId = parentACandidate
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;

        parentACandidate
            .CollectionCandidates.Should()
            .HaveCount(
                2,
                "parent A's `detail.children` array has two items reachable through the inlined `detail` scope"
            );

        var parentAFirstChild = parentACandidate.CollectionCandidates[0];
        parentAFirstChild.OrdinalPath.Should().Equal(0, 0);
        parentAFirstChild.RequestOrder.Should().Be(0);
        parentAFirstChild.SemanticIdentityValues.Should().Equal("A1");
        // Children plan layout: [ChildItemId, ParentItemId, Ordinal, IdentityField0].
        // ParentItemId binds via WriteValueSource.ParentKeyPart(0) so it must point at the
        // owning parent candidate's PhysicalRowIdentity slot 0 (the parent's CollectionItemId).
        parentAFirstChild
            .Values[1]
            .Should()
            .BeSameAs(
                parentACollectionItemId,
                "the parent-key slot resolves to the owning parent candidate's CollectionItemId"
            );
        parentAFirstChild.Values[2].Should().Be(new FlattenedWriteValue.Literal(0));
        parentAFirstChild.Values[3].Should().Be(new FlattenedWriteValue.Literal("A1"));

        var parentASecondChild = parentACandidate.CollectionCandidates[1];
        parentASecondChild.OrdinalPath.Should().Equal(0, 1);
        parentASecondChild.RequestOrder.Should().Be(1);
        parentASecondChild.SemanticIdentityValues.Should().Equal("A2");
        parentASecondChild.Values[1].Should().BeSameAs(parentACollectionItemId);

        var parentBCandidate = parentCandidates[1];
        parentBCandidate.SemanticIdentityValues.Should().Equal("B");
        var parentBCollectionItemId = parentBCandidate
            .Values[0]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        parentBCandidate.CollectionCandidates.Should().HaveCount(1);
        var parentBOnlyChild = parentBCandidate.CollectionCandidates[0];
        parentBOnlyChild.OrdinalPath.Should().Equal(1, 0);
        parentBOnlyChild.SemanticIdentityValues.Should().Equal("B1");
        // Cross-parent key isolation: B's child must reference B's CollectionItemId, not A's.
        parentBOnlyChild.Values[1].Should().BeSameAs(parentBCollectionItemId);
        parentBOnlyChild.Values[1].Should().NotBeSameAs(parentACollectionItemId);
    }

    [Test]
    public void It_rejects_duplicate_inlined_parent_nested_collection_semantic_identity_values_under_the_same_parent()
    {
        var body = JsonNode.Parse(
            """
            {
              "parents": [
                {
                  "identityField0": "A",
                  "detail": {
                    "children": [
                      { "identityField0": "X" },
                      { "identityField0": "X" }
                    ]
                  }
                }
              ]
            }
            """
        )!;

        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.ExistingDocument(DocumentId, _documentUuid),
            _writePlan,
            body,
            EmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);
        act.Should().Throw<RelationalWriteRequestValidationException>();
    }

    private static ResolvedReferenceSet EmptyResolvedReferences() =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
}
