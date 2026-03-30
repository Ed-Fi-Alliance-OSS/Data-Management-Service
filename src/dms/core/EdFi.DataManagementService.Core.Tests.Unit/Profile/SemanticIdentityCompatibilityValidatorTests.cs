// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class SemanticIdentityCompatibilityValidatorTests
{
    protected const string ProfileName = "TestProfile";
    protected const string ResourceName = "StudentSchoolAssociation";

    /// <summary>
    /// Compiled scope descriptors for the shared reference fixture.
    /// Matches the delivery plan's StudentSchoolAssociation example.
    /// </summary>
    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths:
                [
                    "studentReference.studentUniqueId",
                    "schoolReference.schoolId",
                    "entryDate",
                    "entryTypeDescriptor",
                ]
            ),
            new(
                JsonScope: "$.calendarReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["calendarCode", "calendarTypeDescriptor"]
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName", "officialAttendancePeriod"]
            ),
        ];

    protected static ProfileDefinition BuildProfile(ContentTypeDefinition writeContent) =>
        new(
            ProfileName: ProfileName,
            Resources:
            [
                new ResourceProfile(
                    ResourceName: ResourceName,
                    LogicalSchema: null,
                    ReadContentType: null,
                    WriteContentType: writeContent
                ),
            ]
        );

    [TestFixture]
    public class Given_ValidProfile_Exposing_All_Identity_Fields : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties:
                [
                    new PropertyRule("studentReference"),
                    new PropertyRule("schoolReference"),
                    new PropertyRule("entryDate"),
                ],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "classPeriods",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("classPeriodName")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: []
            );

            _result = SemanticIdentityCompatibilityValidator.Validate(
                BuildProfile(writeContent),
                ResourceName,
                SharedFixtureScopes
            );
        }

        [Test]
        public void It_should_return_no_failures()
        {
            _result.Should().BeEmpty();
        }
    }
}
