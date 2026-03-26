// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
}
