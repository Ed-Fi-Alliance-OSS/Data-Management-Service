// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class EffectiveSchemaRequiredMembersBuilderTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Schema_With_Multiple_Scope_Shapes : EffectiveSchemaRequiredMembersBuilderTests
    {
        private IReadOnlyDictionary<string, IReadOnlyList<string>> _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode schema = JsonNode.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "studentUniqueId": { "type": "string" },
                    "schoolReference": {
                      "type": "object",
                      "properties": {
                        "schoolId": { "type": "integer" },
                        "schoolYear": { "type": "integer" }
                      },
                      "required": ["schoolId", "schoolYear"]
                    },
                    "classPeriods": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "classPeriodReference": {
                            "type": "object",
                            "properties": {
                              "classPeriodName": { "type": "string" }
                            },
                            "required": ["classPeriodName"]
                          },
                          "officialAttendancePeriod": { "type": "boolean" }
                        },
                        "required": ["classPeriodReference", "officialAttendancePeriod"]
                      }
                    },
                    "_ext": {
                      "type": "object",
                      "properties": {
                        "sample": {
                          "type": "object",
                          "properties": {
                            "sampleField": { "type": "string" },
                            "sampleAuxField": { "type": "string" }
                          },
                          "required": ["sampleField", "sampleAuxField"]
                        }
                      }
                    }
                  },
                  "required": ["studentUniqueId", "schoolReference"]
                }
                """
            )!;

            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.schoolReference",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.classPeriods[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["classPeriodReference.classPeriodName"],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$._ext.sample",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
            ];

            _result = EffectiveSchemaRequiredMembersBuilder.Build(schema, scopes);
        }

        [Test]
        public void It_extracts_root_required_members_from_top_level_required()
        {
            _result["$"].Should().Equal("studentUniqueId", "schoolReference");
        }

        [Test]
        public void It_extracts_non_collection_required_members_from_inner_required()
        {
            _result["$.schoolReference"].Should().Equal("schoolId", "schoolYear");
        }

        [Test]
        public void It_extracts_collection_item_required_members_from_items_required()
        {
            _result["$.classPeriods[*]"].Should().Equal("classPeriodReference", "officialAttendancePeriod");
        }

        [Test]
        public void It_extracts_extension_scope_required_members_via_ext_namespace()
        {
            _result["$._ext.sample"].Should().Equal("sampleField", "sampleAuxField");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Scope_Not_Locatable_In_The_Schema : EffectiveSchemaRequiredMembersBuilderTests
    {
        private JsonNode _schema = null!;
        private IReadOnlyList<CompiledScopeDescriptor> _scopes = null!;

        [SetUp]
        public void Setup()
        {
            _schema = JsonNode.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "studentUniqueId": { "type": "string" }
                  },
                  "required": ["studentUniqueId"]
                }
                """
            )!;

            _scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.notInSchema",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
            ];
        }

        [Test]
        public void It_throws_invalid_operation_naming_the_unlocatable_scope()
        {
            Action act = () => EffectiveSchemaRequiredMembersBuilder.Build(_schema, _scopes);

            act.Should().Throw<InvalidOperationException>().WithMessage("*$.notInSchema*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Scope_Whose_Schema_Node_Has_No_Required_Array
        : EffectiveSchemaRequiredMembersBuilderTests
    {
        private IReadOnlyDictionary<string, IReadOnlyList<string>> _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode schema = JsonNode.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "metadata": {
                      "type": "object",
                      "properties": { "tag": { "type": "string" } }
                    }
                  },
                  "required": ["metadata"]
                }
                """
            )!;

            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$.metadata",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
            ];

            _result = EffectiveSchemaRequiredMembersBuilder.Build(schema, scopes);
        }

        [Test]
        public void It_records_an_empty_required_list_for_that_scope()
        {
            _result["$.metadata"].Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Nested_Collection_Item_Path : EffectiveSchemaRequiredMembersBuilderTests
    {
        private IReadOnlyDictionary<string, IReadOnlyList<string>> _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode schema = JsonNode.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "addresses": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "addressTypeDescriptor": { "type": "string" },
                          "periods": {
                            "type": "array",
                            "items": {
                              "type": "object",
                              "properties": {
                                "beginDate": { "type": "string" },
                                "endDate": { "type": "string" }
                              },
                              "required": ["beginDate"]
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """
            )!;

            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$.addresses[*].periods[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$.addresses[*]",
                    CollectionAncestorsInOrder: ["$.addresses[*]"],
                    SemanticIdentityRelativePathsInOrder: ["beginDate"],
                    CanonicalScopeRelativeMemberPaths: []
                ),
            ];

            _result = EffectiveSchemaRequiredMembersBuilder.Build(schema, scopes);
        }

        [Test]
        public void It_walks_through_multiple_array_items_layers()
        {
            _result["$.addresses[*].periods[*]"].Should().Equal("beginDate");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_Aligned_Extension_Scopes : EffectiveSchemaRequiredMembersBuilderTests
    {
        // Collection-aligned _ext was a key risk for the original Slice 5 blocker:
        // an _ext attached inside a collection item, optionally containing its own
        // nested collection, must surface its required members per scope so a
        // hidden required non-identity member there still drives non-creatable.
        private IReadOnlyDictionary<string, IReadOnlyList<string>> _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode schema = JsonNode.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "classPeriods": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "classPeriodName": { "type": "string" },
                          "_ext": {
                            "type": "object",
                            "properties": {
                              "sample": {
                                "type": "object",
                                "properties": {
                                  "extraField": { "type": "string" },
                                  "extraAuxField": { "type": "string" },
                                  "subActivities": {
                                    "type": "array",
                                    "items": {
                                      "type": "object",
                                      "properties": {
                                        "activityCode": { "type": "string" },
                                        "activityNote": { "type": "string" }
                                      },
                                      "required": ["activityCode", "activityNote"]
                                    }
                                  }
                                },
                                "required": ["extraField", "extraAuxField"]
                              }
                            }
                          }
                        },
                        "required": ["classPeriodName"]
                      }
                    }
                  }
                }
                """
            )!;

            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$.classPeriods[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.classPeriods[*]._ext.sample",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.classPeriods[*]",
                    CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.classPeriods[*]._ext.sample.subActivities[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$.classPeriods[*]._ext.sample",
                    CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                    SemanticIdentityRelativePathsInOrder: ["activityCode"],
                    CanonicalScopeRelativeMemberPaths: []
                ),
            ];

            _result = EffectiveSchemaRequiredMembersBuilder.Build(schema, scopes);
        }

        [Test]
        public void It_resolves_the_collection_aligned_extension_scope()
        {
            _result["$.classPeriods[*]._ext.sample"].Should().Equal("extraField", "extraAuxField");
        }

        [Test]
        public void It_resolves_a_collection_nested_under_the_collection_aligned_extension()
        {
            _result["$.classPeriods[*]._ext.sample.subActivities[*]"]
                .Should()
                .Equal("activityCode", "activityNote");
        }
    }
}
