// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class ProfileTreeNavigatorTests
{
    // -----------------------------------------------------------------------
    //  Shared profile fixtures
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a ContentTypeDefinition for the shared StudentSchoolAssociation-like
    /// profile with IncludeOnly at root, an ObjectRule for calendarReference, a
    /// CollectionRule for classPeriods (with nested meetingTimes), and an
    /// ExtensionRule for the "sample" extension.
    /// </summary>
    protected static ContentTypeDefinition BuildSharedIncludeOnlyProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
            ],
            Objects:
            [
                new ObjectRule(
                    Name: "calendarReference",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties:
                    [
                        new PropertyRule("calendarCode"),
                        new PropertyRule("calendarTypeDescriptor"),
                    ],
                    NestedObjects: null,
                    Collections: null,
                    Extensions: null
                ),
            ],
            Collections:
            [
                new CollectionRule(
                    Name: "classPeriods",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties:
                    [
                        new PropertyRule("classPeriodName"),
                        new PropertyRule("officialAttendancePeriod"),
                    ],
                    NestedObjects: null,
                    NestedCollections:
                    [
                        new CollectionRule(
                            Name: "meetingTimes",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("startTime"), new PropertyRule("endTime")],
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
            Extensions:
            [
                new ExtensionRule(
                    Name: "sample",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("sampleField")],
                    Objects: null,
                    Collections:
                    [
                        new CollectionRule(
                            Name: "extActivities",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties:
                            [
                                new PropertyRule("activityName"),
                                new PropertyRule("activityDescription"),
                            ],
                            NestedObjects: null,
                            NestedCollections: null,
                            Extensions: null,
                            ItemFilter: null
                        ),
                    ]
                ),
            ]
        );

    /// <summary>
    /// Builds an IncludeAll root definition with no explicit rules.
    /// </summary>
    protected static ContentTypeDefinition BuildIncludeAllProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [],
            Objects: [],
            Collections: [],
            Extensions: []
        );

    // -----------------------------------------------------------------------
    //  Given_Root_Scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Root_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;
        private ContentTypeDefinition _writeContent = null!;

        [SetUp]
        public void Setup()
        {
            _writeContent = BuildSharedIncludeOnlyProfile();
            var navigator = new ProfileTreeNavigator(_writeContent);
            _result = navigator.Navigate("$");
        }

        [Test]
        public void It_should_return_a_non_null_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_IncludeOnly_MemberSelection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_should_expose_root_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().Contain("studentReference");
            _result!.Value.ExplicitPropertyNames.Should().Contain("schoolReference");
            _result!.Value.ExplicitPropertyNames.Should().Contain("entryDate");
        }

        [Test]
        public void It_should_expose_collection_rules_by_name()
        {
            _result!.Value.CollectionsByName.Should().ContainKey("classPeriods");
        }

        [Test]
        public void It_should_expose_object_rules_by_name()
        {
            _result!.Value.ObjectsByName.Should().ContainKey("calendarReference");
        }

        [Test]
        public void It_should_expose_extension_rules_by_name()
        {
            _result!.Value.ExtensionsByName.Should().NotBeNull();
            _result!.Value.ExtensionsByName!.Should().ContainKey("sample");
        }
    }

    // -----------------------------------------------------------------------
    //  Given_Non_Collection_Child_Scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Non_Collection_Child_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = BuildSharedIncludeOnlyProfile();
            var navigator = new ProfileTreeNavigator(writeContent);
            _result = navigator.Navigate("$.calendarReference");
        }

        [Test]
        public void It_should_return_a_non_null_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_IncludeOnly_MemberSelection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_should_expose_the_object_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().Contain("calendarCode");
            _result!.Value.ExplicitPropertyNames.Should().Contain("calendarTypeDescriptor");
        }
    }

    // -----------------------------------------------------------------------
    //  Given_Collection_Scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = BuildSharedIncludeOnlyProfile();
            var navigator = new ProfileTreeNavigator(writeContent);
            _result = navigator.Navigate("$.classPeriods[*]");
        }

        [Test]
        public void It_should_return_a_non_null_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_IncludeOnly_MemberSelection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_should_expose_the_collection_item_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().Contain("classPeriodName");
            _result!.Value.ExplicitPropertyNames.Should().Contain("officialAttendancePeriod");
        }

        [Test]
        public void It_should_expose_nested_collection_rules()
        {
            _result!.Value.CollectionsByName.Should().ContainKey("meetingTimes");
        }
    }

    // -----------------------------------------------------------------------
    //  Given_Nested_Collection_Scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Nested_Collection_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = BuildSharedIncludeOnlyProfile();
            var navigator = new ProfileTreeNavigator(writeContent);
            _result = navigator.Navigate("$.classPeriods[*].meetingTimes[*]");
        }

        [Test]
        public void It_should_return_a_non_null_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_IncludeOnly_MemberSelection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_should_expose_the_nested_collection_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().Contain("startTime");
            _result!.Value.ExplicitPropertyNames.Should().Contain("endTime");
        }
    }

    // -----------------------------------------------------------------------
    //  Given_Extension_Scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Extension_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = BuildSharedIncludeOnlyProfile();
            var navigator = new ProfileTreeNavigator(writeContent);
            _result = navigator.Navigate("$._ext.sample");
        }

        [Test]
        public void It_should_return_a_non_null_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_IncludeOnly_MemberSelection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_should_expose_the_extension_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().Contain("sampleField");
        }

        [Test]
        public void It_should_expose_extension_collection_rules()
        {
            _result!.Value.CollectionsByName.Should().ContainKey("extActivities");
        }
    }

    // -----------------------------------------------------------------------
    //  Given_Extension_Collection_Within_Root_Extension
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Extension_Collection_Within_Root_Extension : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = BuildSharedIncludeOnlyProfile();
            var navigator = new ProfileTreeNavigator(writeContent);
            _result = navigator.Navigate("$._ext.sample.extActivities[*]");
        }

        [Test]
        public void It_should_return_a_non_null_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_IncludeOnly_MemberSelection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_should_expose_the_extension_collection_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().Contain("activityName");
            _result!.Value.ExplicitPropertyNames.Should().Contain("activityDescription");
        }
    }

    // -----------------------------------------------------------------------
    //  Given_Scope_Not_In_IncludeOnly_Profile
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Scope_Not_In_IncludeOnly_Profile : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            // Root IncludeOnly profile that lists no objects — calendarReference is hidden
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

            var navigator = new ProfileTreeNavigator(writeContent);
            _result = navigator.Navigate("$.calendarReference");
        }

        [Test]
        public void It_should_return_null()
        {
            _result.Should().BeNull();
        }
    }

    // -----------------------------------------------------------------------
    //  Given_IncludeAll_Root_With_No_Rules
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeAll_Root_With_No_Rules : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _resultCollection;
        private ProfileTreeNode? _resultObject;

        [SetUp]
        public void Setup()
        {
            ContentTypeDefinition writeContent = BuildIncludeAllProfile();
            var navigator = new ProfileTreeNavigator(writeContent);
            _resultCollection = navigator.Navigate("$.classPeriods[*]");
            _resultObject = navigator.Navigate("$.calendarReference");
        }

        [Test]
        public void It_should_return_IncludeAll_node_for_unlisted_collection()
        {
            _resultCollection.Should().NotBeNull();
            _resultCollection!.Value.MemberSelection.Should().Be(MemberSelection.IncludeAll);
        }

        [Test]
        public void It_should_return_IncludeAll_node_for_unlisted_object()
        {
            _resultObject.Should().NotBeNull();
            _resultObject!.Value.MemberSelection.Should().Be(MemberSelection.IncludeAll);
        }
    }

    // -----------------------------------------------------------------------
    //  Given_ExcludeOnly_Root_With_Unlisted_Collection
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_ExcludeOnly_Root_With_Unlisted_Collection : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            // ExcludeOnly root, no explicit collection sub-rules
            // → any collection not listed is visible with IncludeAll
            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.ExcludeOnly,
                Properties: [new PropertyRule("entryTypeDescriptor")],
                Objects: [],
                Collections: [],
                Extensions: []
            );

            var navigator = new ProfileTreeNavigator(writeContent);
            _result = navigator.Navigate("$.classPeriods[*]");
        }

        [Test]
        public void It_should_return_a_non_null_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_IncludeAll_MemberSelection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeAll);
        }
    }
}
