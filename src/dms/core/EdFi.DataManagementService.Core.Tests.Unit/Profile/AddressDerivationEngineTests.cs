// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class AddressDerivationEngineTests
{
    /// <summary>
    /// Shared scope catalog modeled on the delivery plan's StudentSchoolAssociation-like
    /// reference fixture.
    /// </summary>
    protected static IReadOnlyList<CompiledScopeDescriptor> BuildTestScopeCatalog() =>
        [
            // Root scope
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths:
                [
                    "studentReference",
                    "schoolReference",
                    "entryDate",
                    "entryTypeDescriptor",
                ]
            ),
            // Root-adjacent 1:1 scope
            new(
                JsonScope: "$.calendarReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["calendarCode", "calendarTypeDescriptor"]
            ),
            // Single-level collection
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName", "officialAttendancePeriod"]
            ),
            // Nested collection (two-level)
            new(
                JsonScope: "$.classPeriods[*].meetingTimes[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: ["startTime", "endTime"],
                CanonicalScopeRelativeMemberPaths: ["startTime", "endTime"]
            ),
            // Root-level _ext scope
            new(
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["specialNote"]
            ),
            // Collection-aligned _ext child collection
            new(
                JsonScope: "$._ext.sample.extActivities[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["activityName"],
                CanonicalScopeRelativeMemberPaths: ["activityName", "activityDate"]
            ),
        ];

    [TestFixture]
    public class Given_root_scope_address_derivation : AddressDerivationEngineTests
    {
        private ScopeInstanceAddress _result = null!;

        [SetUp]
        public void Setup()
        {
            var engine = new AddressDerivationEngine(BuildTestScopeCatalog());
            _result = engine.DeriveScopeInstanceAddress("$", []);
        }

        [Test]
        public void It_should_produce_root_JsonScope()
        {
            _result.JsonScope.Should().Be("$");
        }

        [Test]
        public void It_should_have_empty_ancestor_list()
        {
            _result.AncestorCollectionInstances.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_root_adjacent_one_to_one_scope_address_derivation : AddressDerivationEngineTests
    {
        private ScopeInstanceAddress _result = null!;

        [SetUp]
        public void Setup()
        {
            var engine = new AddressDerivationEngine(BuildTestScopeCatalog());
            _result = engine.DeriveScopeInstanceAddress("$.calendarReference", []);
        }

        [Test]
        public void It_should_produce_correct_JsonScope()
        {
            _result.JsonScope.Should().Be("$.calendarReference");
        }

        [Test]
        public void It_should_have_empty_ancestor_list()
        {
            _result.AncestorCollectionInstances.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_single_level_collection_address_derivation : AddressDerivationEngineTests
    {
        private CollectionRowAddress _result = null!;

        [SetUp]
        public void Setup()
        {
            var engine = new AddressDerivationEngine(BuildTestScopeCatalog());
            var collectionItem = new JsonObject { ["classPeriodName"] = "First Period" };

            _result = engine.DeriveCollectionRowAddress("$.classPeriods[*]", collectionItem, []);
        }

        [Test]
        public void It_should_produce_correct_JsonScope()
        {
            _result.JsonScope.Should().Be("$.classPeriods[*]");
        }

        [Test]
        public void It_should_have_root_as_parent_address()
        {
            _result.ParentAddress.JsonScope.Should().Be("$");
        }

        [Test]
        public void It_should_have_empty_parent_ancestor_list()
        {
            _result.ParentAddress.AncestorCollectionInstances.Should().BeEmpty();
        }

        [Test]
        public void It_should_have_one_semantic_identity_part()
        {
            _result.SemanticIdentityInOrder.Should().HaveCount(1);
        }

        [Test]
        public void It_should_have_correct_identity_relative_path()
        {
            _result.SemanticIdentityInOrder[0].RelativePath.Should().Be("classPeriodName");
        }

        [Test]
        public void It_should_have_correct_identity_value()
        {
            _result.SemanticIdentityInOrder[0].Value!.ToString().Should().Be("First Period");
        }

        [Test]
        public void It_should_mark_identity_as_present()
        {
            _result.SemanticIdentityInOrder[0].IsPresent.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_nested_collection_address_derivation : AddressDerivationEngineTests
    {
        private CollectionRowAddress _result = null!;

        [SetUp]
        public void Setup()
        {
            var engine = new AddressDerivationEngine(BuildTestScopeCatalog());

            var classPeriodItem = new JsonObject { ["classPeriodName"] = "First Period" };
            var meetingTimeItem = new JsonObject { ["startTime"] = "09:00:00", ["endTime"] = "10:00:00" };

            _result = engine.DeriveCollectionRowAddress(
                "$.classPeriods[*].meetingTimes[*]",
                meetingTimeItem,
                [new AncestorItemContext("$.classPeriods[*]", classPeriodItem)]
            );
        }

        [Test]
        public void It_should_produce_correct_JsonScope()
        {
            _result.JsonScope.Should().Be("$.classPeriods[*].meetingTimes[*]");
        }

        [Test]
        public void It_should_have_parent_as_classPeriods_scope()
        {
            _result.ParentAddress.JsonScope.Should().Be("$.classPeriods[*]");
        }

        [Test]
        public void It_should_have_one_ancestor_in_parent()
        {
            _result.ParentAddress.AncestorCollectionInstances.Should().HaveCount(1);
        }

        [Test]
        public void It_should_have_classPeriods_as_ancestor()
        {
            _result.ParentAddress.AncestorCollectionInstances[0].JsonScope.Should().Be("$.classPeriods[*]");
        }

        [Test]
        public void It_should_have_ancestor_identity_from_classPeriod_item()
        {
            _result
                .ParentAddress.AncestorCollectionInstances[0]
                .SemanticIdentityInOrder.Should()
                .HaveCount(1);
            _result
                .ParentAddress.AncestorCollectionInstances[0]
                .SemanticIdentityInOrder[0]
                .RelativePath.Should()
                .Be("classPeriodName");
            _result
                .ParentAddress.AncestorCollectionInstances[0]
                .SemanticIdentityInOrder[0]
                .Value!.ToString()
                .Should()
                .Be("First Period");
        }

        [Test]
        public void It_should_have_two_semantic_identity_parts()
        {
            _result.SemanticIdentityInOrder.Should().HaveCount(2);
        }

        [Test]
        public void It_should_have_startTime_as_first_identity()
        {
            _result.SemanticIdentityInOrder[0].RelativePath.Should().Be("startTime");
            _result.SemanticIdentityInOrder[0].Value!.ToString().Should().Be("09:00:00");
            _result.SemanticIdentityInOrder[0].IsPresent.Should().BeTrue();
        }

        [Test]
        public void It_should_have_endTime_as_second_identity()
        {
            _result.SemanticIdentityInOrder[1].RelativePath.Should().Be("endTime");
            _result.SemanticIdentityInOrder[1].Value!.ToString().Should().Be("10:00:00");
            _result.SemanticIdentityInOrder[1].IsPresent.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_root_level_ext_scope_address_derivation : AddressDerivationEngineTests
    {
        private ScopeInstanceAddress _result = null!;

        [SetUp]
        public void Setup()
        {
            var engine = new AddressDerivationEngine(BuildTestScopeCatalog());
            _result = engine.DeriveScopeInstanceAddress("$._ext.sample", []);
        }

        [Test]
        public void It_should_produce_correct_JsonScope()
        {
            _result.JsonScope.Should().Be("$._ext.sample");
        }

        [Test]
        public void It_should_have_empty_ancestor_list()
        {
            _result.AncestorCollectionInstances.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_collection_aligned_ext_child_collection_address_derivation
        : AddressDerivationEngineTests
    {
        private CollectionRowAddress _result = null!;

        [SetUp]
        public void Setup()
        {
            var engine = new AddressDerivationEngine(BuildTestScopeCatalog());
            var extActivityItem = new JsonObject
            {
                ["activityName"] = "Chess Club",
                ["activityDate"] = "2026-01-15",
            };

            _result = engine.DeriveCollectionRowAddress(
                "$._ext.sample.extActivities[*]",
                extActivityItem,
                []
            );
        }

        [Test]
        public void It_should_produce_correct_JsonScope()
        {
            _result.JsonScope.Should().Be("$._ext.sample.extActivities[*]");
        }

        [Test]
        public void It_should_have_root_as_parent_not_ext_scope()
        {
            _result.ParentAddress.JsonScope.Should().Be("$");
        }

        [Test]
        public void It_should_have_empty_parent_ancestor_list()
        {
            _result.ParentAddress.AncestorCollectionInstances.Should().BeEmpty();
        }

        [Test]
        public void It_should_have_one_semantic_identity_part()
        {
            _result.SemanticIdentityInOrder.Should().HaveCount(1);
        }

        [Test]
        public void It_should_have_correct_identity_value()
        {
            _result.SemanticIdentityInOrder[0].RelativePath.Should().Be("activityName");
            _result.SemanticIdentityInOrder[0].Value!.ToString().Should().Be("Chess Club");
            _result.SemanticIdentityInOrder[0].IsPresent.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_same_json_data_from_request_and_stored_sides : AddressDerivationEngineTests
    {
        private CollectionRowAddress _requestResult = null!;
        private CollectionRowAddress _storedResult = null!;

        [SetUp]
        public void Setup()
        {
            var catalog = BuildTestScopeCatalog();
            var requestEngine = new AddressDerivationEngine(catalog);
            var storedEngine = new AddressDerivationEngine(catalog);

            var collectionItem = new JsonObject { ["classPeriodName"] = "First Period" };
            var identicalItem = new JsonObject { ["classPeriodName"] = "First Period" };

            _requestResult = requestEngine.DeriveCollectionRowAddress(
                "$.classPeriods[*]",
                collectionItem,
                []
            );

            _storedResult = storedEngine.DeriveCollectionRowAddress("$.classPeriods[*]", identicalItem, []);
        }

        [Test]
        public void It_should_produce_identical_JsonScope()
        {
            _requestResult.JsonScope.Should().Be(_storedResult.JsonScope);
        }

        [Test]
        public void It_should_produce_identical_parent_address()
        {
            _requestResult.ParentAddress.JsonScope.Should().Be(_storedResult.ParentAddress.JsonScope);
            _requestResult
                .ParentAddress.AncestorCollectionInstances.Length.Should()
                .Be(_storedResult.ParentAddress.AncestorCollectionInstances.Length);
        }

        [Test]
        public void It_should_produce_identical_semantic_identity()
        {
            _requestResult
                .SemanticIdentityInOrder.Length.Should()
                .Be(_storedResult.SemanticIdentityInOrder.Length);
            _requestResult
                .SemanticIdentityInOrder[0]
                .RelativePath.Should()
                .Be(_storedResult.SemanticIdentityInOrder[0].RelativePath);
            _requestResult
                .SemanticIdentityInOrder[0]
                .Value!.ToString()
                .Should()
                .Be(_storedResult.SemanticIdentityInOrder[0].Value!.ToString());
            _requestResult
                .SemanticIdentityInOrder[0]
                .IsPresent.Should()
                .Be(_storedResult.SemanticIdentityInOrder[0].IsPresent);
        }
    }

    [TestFixture]
    public class Given_missing_vs_null_semantic_identity_member : AddressDerivationEngineTests
    {
        private CollectionRowAddress _resultWithNull = null!;
        private CollectionRowAddress _resultWithMissing = null!;

        [SetUp]
        public void Setup()
        {
            var engine = new AddressDerivationEngine(BuildTestScopeCatalog());

            // Explicit null: property exists but value is null
            var itemWithNull = new JsonObject { ["classPeriodName"] = null };

            // Missing: property does not exist at all
            var itemWithMissing = new JsonObject { ["somethingElse"] = "irrelevant" };

            _resultWithNull = engine.DeriveCollectionRowAddress("$.classPeriods[*]", itemWithNull, []);

            _resultWithMissing = engine.DeriveCollectionRowAddress("$.classPeriods[*]", itemWithMissing, []);
        }

        [Test]
        public void It_should_mark_explicit_null_as_present()
        {
            _resultWithNull.SemanticIdentityInOrder[0].IsPresent.Should().BeTrue();
        }

        [Test]
        public void It_should_have_null_value_for_explicit_null()
        {
            _resultWithNull.SemanticIdentityInOrder[0].Value.Should().BeNull();
        }

        [Test]
        public void It_should_mark_missing_property_as_not_present()
        {
            _resultWithMissing.SemanticIdentityInOrder[0].IsPresent.Should().BeFalse();
        }

        [Test]
        public void It_should_have_null_value_for_missing_property()
        {
            _resultWithMissing.SemanticIdentityInOrder[0].Value.Should().BeNull();
        }
    }
}
