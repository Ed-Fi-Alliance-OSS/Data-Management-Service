// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileDefinitionParserTests
{
    [TestFixture]
    public class Given_Invalid_Input : ProfileDefinitionParserTests
    {
        [Test]
        public void It_fails_for_null_input()
        {
            var result = ProfileDefinitionParser.Parse(null!);

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("empty or null");
        }

        [Test]
        public void It_fails_for_empty_input()
        {
            var result = ProfileDefinitionParser.Parse("");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("empty or null");
        }

        [Test]
        public void It_fails_for_whitespace_input()
        {
            var result = ProfileDefinitionParser.Parse("   ");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("empty or null");
        }

        [Test]
        public void It_fails_for_invalid_xml()
        {
            var result = ProfileDefinitionParser.Parse("<invalid>");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Failed to parse");
        }

        [Test]
        public void It_fails_for_wrong_root_element()
        {
            var result = ProfileDefinitionParser.Parse(
                "<NotProfile name=\"Test\"><Resource name=\"Student\"/></NotProfile>"
            );

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("'Profile' root element");
        }

        [Test]
        public void It_fails_for_missing_profile_name()
        {
            var result = ProfileDefinitionParser.Parse("<Profile><Resource name=\"Student\"/></Profile>");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("'name' attribute");
        }

        [Test]
        public void It_fails_for_empty_profile_name()
        {
            var result = ProfileDefinitionParser.Parse(
                "<Profile name=\"\"><Resource name=\"Student\"/></Profile>"
            );

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("'name' attribute");
        }

        [Test]
        public void It_fails_for_no_resources()
        {
            var result = ProfileDefinitionParser.Parse("<Profile name=\"TestProfile\"></Profile>");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("at least one Resource");
        }

        [Test]
        public void It_fails_for_resources_without_names()
        {
            var result = ProfileDefinitionParser.Parse("<Profile name=\"TestProfile\"><Resource/></Profile>");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("at least one Resource");
        }
    }

    [TestFixture]
    public class Given_Valid_Simple_Profile : ProfileDefinitionParserTests
    {
        [Test]
        public void It_parses_profile_with_single_resource()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.IsSuccess.Should().BeTrue();
            result.Definition.Should().NotBeNull();
            result.Definition!.ProfileName.Should().Be("TestProfile");
            result.Definition.Resources.Should().HaveCount(1);
            result.Definition.Resources[0].ResourceName.Should().Be("Student");
        }

        [Test]
        public void It_parses_profile_with_multiple_resources()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll"/>
                    </Resource>
                    <Resource name="School">
                        <WriteContentType memberSelection="IncludeAll"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.IsSuccess.Should().BeTrue();
            result.Definition!.Resources.Should().HaveCount(2);
        }

        [Test]
        public void It_parses_resource_with_logical_schema()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student" logicalSchema="ed-fi">
                        <ReadContentType memberSelection="IncludeAll"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.IsSuccess.Should().BeTrue();
            result.Definition!.Resources[0].LogicalSchema.Should().Be("ed-fi");
        }
    }

    [TestFixture]
    public class Given_Content_Types : ProfileDefinitionParserTests
    {
        [Test]
        public void It_parses_read_content_type()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeOnly">
                            <Property name="firstName"/>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.IsSuccess.Should().BeTrue();
            result.Definition!.Resources[0].ReadContentType.Should().NotBeNull();
            result.Definition!.Resources[0].WriteContentType.Should().BeNull();
        }

        [Test]
        public void It_parses_write_content_type()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <WriteContentType memberSelection="ExcludeOnly">
                            <Property name="id"/>
                        </WriteContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.IsSuccess.Should().BeTrue();
            result.Definition!.Resources[0].WriteContentType.Should().NotBeNull();
            result.Definition!.Resources[0].ReadContentType.Should().BeNull();
        }

        [Test]
        public void It_parses_both_content_types()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll"/>
                        <WriteContentType memberSelection="IncludeAll"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.IsSuccess.Should().BeTrue();
            result.Definition!.Resources[0].ReadContentType.Should().NotBeNull();
            result.Definition!.Resources[0].WriteContentType.Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_Member_Selection : ProfileDefinitionParserTests
    {
        [Test]
        public void It_parses_include_only()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeOnly"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result
                .Definition!.Resources[0]
                .ReadContentType!.MemberSelection.Should()
                .Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_parses_exclude_only()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="ExcludeOnly"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result
                .Definition!.Resources[0]
                .ReadContentType!.MemberSelection.Should()
                .Be(MemberSelection.ExcludeOnly);
        }

        [Test]
        public void It_parses_include_all()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result
                .Definition!.Resources[0]
                .ReadContentType!.MemberSelection.Should()
                .Be(MemberSelection.IncludeAll);
        }

        [Test]
        public void It_defaults_to_include_all_for_missing_member_selection()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result
                .Definition!.Resources[0]
                .ReadContentType!.MemberSelection.Should()
                .Be(MemberSelection.IncludeAll);
        }

        [Test]
        public void It_is_case_insensitive_for_member_selection()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="INCLUDEONLY"/>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result
                .Definition!.Resources[0]
                .ReadContentType!.MemberSelection.Should()
                .Be(MemberSelection.IncludeOnly);
        }
    }

    [TestFixture]
    public class Given_Properties : ProfileDefinitionParserTests
    {
        [Test]
        public void It_parses_multiple_properties()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeOnly">
                            <Property name="firstName"/>
                            <Property name="lastName"/>
                            <Property name="birthDate"/>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Properties.Should().HaveCount(3);
            result.Definition!.Resources[0].ReadContentType!.Properties[0].Name.Should().Be("firstName");
        }

        [Test]
        public void It_ignores_properties_without_names()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeOnly">
                            <Property name="firstName"/>
                            <Property/>
                            <Property name=""/>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Properties.Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_Objects : ProfileDefinitionParserTests
    {
        [Test]
        public void It_parses_object_with_properties()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Object name="studentIdentificationCodes" memberSelection="IncludeOnly">
                                <Property name="identificationCode"/>
                            </Object>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Objects.Should().HaveCount(1);
            result
                .Definition!.Resources[0]
                .ReadContentType!.Objects[0]
                .Name.Should()
                .Be("studentIdentificationCodes");
            result.Definition!.Resources[0].ReadContentType!.Objects[0].Properties.Should().HaveCount(1);
        }

        [Test]
        public void It_parses_nested_objects()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Object name="outer" memberSelection="IncludeAll">
                                <Object name="inner" memberSelection="IncludeOnly">
                                    <Property name="value"/>
                                </Object>
                            </Object>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Objects[0].NestedObjects.Should().HaveCount(1);
            result
                .Definition!.Resources[0]
                .ReadContentType!.Objects[0]
                .NestedObjects![0]
                .Name.Should()
                .Be("inner");
        }

        [Test]
        public void It_ignores_objects_without_names()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Object name="valid"/>
                            <Object/>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Objects.Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_Collections : ProfileDefinitionParserTests
    {
        [Test]
        public void It_parses_collection()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Collection name="addresses" memberSelection="IncludeOnly">
                                <Property name="streetAddress"/>
                            </Collection>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Collections.Should().HaveCount(1);
            result.Definition!.Resources[0].ReadContentType!.Collections[0].Name.Should().Be("addresses");
        }

        [Test]
        public void It_parses_collection_with_filter()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Collection name="addresses" memberSelection="IncludeOnly">
                                <Filter propertyName="addressTypeDescriptor" filterMode="IncludeOnly">
                                    <Value>uri://ed-fi.org/AddressTypeDescriptor#Home</Value>
                                    <Value>uri://ed-fi.org/AddressTypeDescriptor#Mailing</Value>
                                </Filter>
                            </Collection>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            var filter = result.Definition!.Resources[0].ReadContentType!.Collections[0].ItemFilter;
            filter.Should().NotBeNull();
            filter!.PropertyName.Should().Be("addressTypeDescriptor");
            filter.FilterMode.Should().Be(FilterMode.IncludeOnly);
            filter.Values.Should().HaveCount(2);
        }

        [Test]
        public void It_parses_collection_with_exclude_filter()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Collection name="addresses" memberSelection="IncludeAll">
                                <Filter propertyName="addressTypeDescriptor" filterMode="ExcludeOnly">
                                    <Value>uri://ed-fi.org/AddressTypeDescriptor#Temporary</Value>
                                </Filter>
                            </Collection>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result
                .Definition!.Resources[0]
                .ReadContentType!.Collections[0]
                .ItemFilter!.FilterMode.Should()
                .Be(FilterMode.ExcludeOnly);
        }

        [Test]
        public void It_ignores_filter_without_property_name()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Collection name="addresses" memberSelection="IncludeAll">
                                <Filter>
                                    <Value>uri://ed-fi.org/AddressTypeDescriptor#Home</Value>
                                </Filter>
                            </Collection>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Collections[0].ItemFilter.Should().BeNull();
        }

        [Test]
        public void It_ignores_filter_without_values()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Collection name="addresses" memberSelection="IncludeAll">
                                <Filter propertyName="addressTypeDescriptor"/>
                            </Collection>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Collections[0].ItemFilter.Should().BeNull();
        }

        [Test]
        public void It_parses_nested_collections()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Collection name="outer" memberSelection="IncludeAll">
                                <Collection name="inner" memberSelection="IncludeOnly"/>
                            </Collection>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result
                .Definition!.Resources[0]
                .ReadContentType!.Collections[0]
                .NestedCollections.Should()
                .HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_Extensions : ProfileDefinitionParserTests
    {
        [Test]
        public void It_parses_extension()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Extension name="Sample" memberSelection="IncludeOnly">
                                <Property name="customField"/>
                            </Extension>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Extensions.Should().HaveCount(1);
            result.Definition!.Resources[0].ReadContentType!.Extensions[0].Name.Should().Be("Sample");
        }

        [Test]
        public void It_parses_extension_with_objects_and_collections()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Extension name="Sample" memberSelection="IncludeOnly">
                                <Object name="customObject"/>
                                <Collection name="customCollection"/>
                            </Extension>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Extensions[0].Objects.Should().HaveCount(1);
            result.Definition!.Resources[0].ReadContentType!.Extensions[0].Collections.Should().HaveCount(1);
        }

        [Test]
        public void It_ignores_extensions_without_names()
        {
            string xml = """
                <Profile name="TestProfile">
                    <Resource name="Student">
                        <ReadContentType memberSelection="IncludeAll">
                            <Extension name="Valid"/>
                            <Extension/>
                        </ReadContentType>
                    </Resource>
                </Profile>
                """;

            var result = ProfileDefinitionParser.Parse(xml);

            result.Definition!.Resources[0].ReadContentType!.Extensions.Should().HaveCount(1);
        }
    }
}
