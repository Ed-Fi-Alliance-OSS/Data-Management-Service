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

    [TestFixture]
    public class Given_Profile_Hiding_Semantic_Identity_Field : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            // IncludeOnly collection lists only officialAttendancePeriod — classPeriodName (identity) is hidden
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
                        Properties: [new PropertyRule("officialAttendancePeriod")],
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
        public void It_should_return_one_failure()
        {
            _result.Should().HaveCount(1);
        }

        [Test]
        public void It_should_identify_the_correct_scope()
        {
            _result[0].JsonScope.Should().Be("$.classPeriods[*]");
        }

        [Test]
        public void It_should_identify_the_hidden_identity_member()
        {
            _result[0].HiddenCanonicalMemberPaths.Should().Equal("classPeriodName");
        }

        [Test]
        public void It_should_include_profile_context()
        {
            _result[0].Context.ProfileName.Should().Be(ProfileName);
            _result[0].Context.ResourceName.Should().Be(ResourceName);
        }

        [Test]
        public void It_should_have_category_1()
        {
            _result[0].Category.Should().Be(ProfileFailureCategory.InvalidProfileDefinition);
        }

        [Test]
        public void It_should_have_semantic_identity_emitter()
        {
            _result[0].Emitter.Should().Be(ProfileFailureEmitter.SemanticIdentityCompatibilityValidation);
        }
    }

    [TestFixture]
    public class Given_Profile_Hiding_NonIdentity_Field_On_Collection
        : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            // classPeriodName (identity) is exposed; officialAttendancePeriod (non-identity) is hidden
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

    [TestFixture]
    public class Given_SingleItem_Scope_Not_Subject_To_Gate : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> nonCollectionScopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1", "field2"]
                ),
                new(
                    JsonScope: "$.calendarReference",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["calendarCode"]
                ),
            ];

            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [],
                Objects: [],
                Collections: [],
                Extensions: []
            );

            _result = SemanticIdentityCompatibilityValidator.Validate(
                BuildProfile(writeContent),
                ResourceName,
                nonCollectionScopes
            );
        }

        [Test]
        public void It_should_return_no_failures()
        {
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_ExcludeOnly_Profile_Excluding_Identity_Field
        : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            // IncludeAll at root, ExcludeOnly within classPeriods — classPeriodName is excluded
            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "classPeriods",
                        MemberSelection: MemberSelection.ExcludeOnly,
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
        public void It_should_return_one_failure()
        {
            _result.Should().HaveCount(1);
        }

        [Test]
        public void It_should_identify_the_hidden_identity_member()
        {
            _result[0].HiddenCanonicalMemberPaths.Should().Equal("classPeriodName");
        }
    }

    [TestFixture]
    public class Given_IncludeAll_Profile : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections: [],
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

    [TestFixture]
    public class Given_Collection_Entirely_Hidden_By_IncludeOnly : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            // classPeriods not listed under IncludeOnly — hidden entirely — no conflict
            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties:
                [
                    new PropertyRule("studentReference"),
                    new PropertyRule("schoolReference"),
                    new PropertyRule("entryDate"),
                ],
                Objects: [],
                Collections: [],
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

    [TestFixture]
    public class Given_No_WriteContentType_For_Resource : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            ProfileDefinition profile = new(
                ProfileName: ProfileName,
                Resources:
                [
                    new ResourceProfile(
                        ResourceName: ResourceName,
                        LogicalSchema: null,
                        ReadContentType: new ContentTypeDefinition(
                            MemberSelection: MemberSelection.IncludeAll,
                            Properties: [],
                            Objects: [],
                            Collections: [],
                            Extensions: []
                        ),
                        WriteContentType: null
                    ),
                ]
            );

            _result = SemanticIdentityCompatibilityValidator.Validate(
                profile,
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

    [TestFixture]
    public class Given_SharedFixture_RestrictedAssociationWrite_Profile
        : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            // RestrictedAssociation-Write from delivery plan §Shared Reference Fixture:
            // Exposes: studentReference, schoolReference, entryDate, classPeriods.classPeriodName
            // Hides: entryTypeDescriptor, entire $.calendarReference, classPeriods.officialAttendancePeriod
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
                new ProfileDefinition(
                    ProfileName: "RestrictedAssociation-Write",
                    Resources:
                    [
                        new ResourceProfile(
                            ResourceName: ResourceName,
                            LogicalSchema: null,
                            ReadContentType: null,
                            WriteContentType: writeContent
                        ),
                    ]
                ),
                ResourceName,
                SharedFixtureScopes
            );
        }

        [Test]
        public void It_should_pass_because_identity_field_is_exposed()
        {
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Collection_In_Extension_Hiding_Identity_Field
        : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$._ext.sample.things[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$._ext.sample",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["thingName"],
                    CanonicalScopeRelativeMemberPaths: ["thingName", "thingValue"]
                ),
            ];

            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects: [],
                Collections: [],
                Extensions:
                [
                    new ExtensionRule(
                        Name: "sample",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: null,
                        Objects: null,
                        Collections:
                        [
                            new CollectionRule(
                                Name: "things",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("thingValue")],
                                NestedObjects: null,
                                NestedCollections: null,
                                Extensions: null,
                                ItemFilter: null
                            ),
                        ]
                    ),
                ]
            );

            _result = SemanticIdentityCompatibilityValidator.Validate(
                BuildProfile(writeContent),
                ResourceName,
                scopes
            );
        }

        [Test]
        public void It_should_return_one_failure()
        {
            _result.Should().HaveCount(1);
        }

        [Test]
        public void It_should_identify_the_extension_collection_scope()
        {
            _result[0].JsonScope.Should().Be("$._ext.sample.things[*]");
        }

        [Test]
        public void It_should_identify_the_hidden_member()
        {
            _result[0].HiddenCanonicalMemberPaths.Should().Equal("thingName");
        }
    }

    [TestFixture]
    public class Given_Nested_Collection_Hiding_Identity_Field : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                    [
                        "addressTypeDescriptor",
                        "city",
                        "stateAbbreviationDescriptor",
                    ]
                ),
                new(
                    JsonScope: "$.addresses[*].periods[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$.addresses[*]",
                    CollectionAncestorsInOrder: ["$.addresses[*]"],
                    SemanticIdentityRelativePathsInOrder: ["beginDate"],
                    CanonicalScopeRelativeMemberPaths: ["beginDate", "endDate"]
                ),
            ];

            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("addressTypeDescriptor"), new PropertyRule("city")],
                        NestedObjects: null,
                        NestedCollections:
                        [
                            new CollectionRule(
                                Name: "periods",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("endDate")],
                                NestedObjects: null,
                                NestedCollections: null,
                                Extensions: null,
                                ItemFilter: null
                            ),
                        ],
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: []
            );

            _result = SemanticIdentityCompatibilityValidator.Validate(
                BuildProfile(writeContent),
                ResourceName,
                scopes
            );
        }

        [Test]
        public void It_should_return_one_failure_for_nested_collection_only()
        {
            // addresses[*] exposes addressTypeDescriptor (identity) — passes
            // addresses[*].periods[*] hides beginDate (identity) — fails
            _result.Should().HaveCount(1);
        }

        [Test]
        public void It_should_identify_the_nested_collection_scope()
        {
            _result[0].JsonScope.Should().Be("$.addresses[*].periods[*]");
        }

        [Test]
        public void It_should_identify_the_hidden_identity_member()
        {
            _result[0].HiddenCanonicalMemberPaths.Should().Equal("beginDate");
        }
    }

    [TestFixture]
    public class Given_Multiple_Collections_With_Multiple_Failures
        : SemanticIdentityCompatibilityValidatorTests
    {
        private IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> _result = null!;

        [SetUp]
        public void Setup()
        {
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
                    JsonScope: "$.collectionA[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["keyA"],
                    CanonicalScopeRelativeMemberPaths: ["keyA", "valueA"]
                ),
                new(
                    JsonScope: "$.collectionB[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["keyB1", "keyB2"],
                    CanonicalScopeRelativeMemberPaths: ["keyB1", "keyB2", "valueB"]
                ),
            ];

            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "collectionA",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("valueA")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                    new CollectionRule(
                        Name: "collectionB",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("keyB1")],
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
                scopes
            );
        }

        [Test]
        public void It_should_return_two_failures()
        {
            _result.Should().HaveCount(2);
        }

        [Test]
        public void It_should_identify_collectionA_hidden_member()
        {
            _result
                .Single(f => f.JsonScope == "$.collectionA[*]")
                .HiddenCanonicalMemberPaths.Should()
                .Equal("keyA");
        }

        [Test]
        public void It_should_identify_collectionB_hidden_member()
        {
            // keyB1 is exposed; keyB2 is hidden
            _result
                .Single(f => f.JsonScope == "$.collectionB[*]")
                .HiddenCanonicalMemberPaths.Should()
                .Equal("keyB2");
        }
    }
}
