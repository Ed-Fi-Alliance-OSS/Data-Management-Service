// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.DataModel;

[TestFixture]
public class ProfileValidationUtilsTests
{
    [TestFixture]
    public class XmlProfileNameMatchesTests
    {
        [Test]
        public void Should_Return_True_When_Profile_Name_Matches()
        {
            // Arrange
            string profileName = "TestProfile";
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.XmlProfileNameMatches(profileName, xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_When_Profile_Name_Does_Not_Match()
        {
            // Arrange
            string profileName = "TestProfile";
            string xml = "<Profile name=\"OtherProfile\"><Resource name=\"Resource1\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.XmlProfileNameMatches(profileName, xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_When_Xml_Is_Invalid()
        {
            // Arrange
            string profileName = "TestProfile";
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\">";

            // Act
            bool result = ProfileValidationUtils.XmlProfileNameMatches(profileName, xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_When_Xml_Has_No_Profile_Element()
        {
            // Arrange
            string profileName = "TestProfile";
            string xml = "<NotAProfile name=\"TestProfile\"></NotAProfile>";

            // Act
            bool result = ProfileValidationUtils.XmlProfileNameMatches(profileName, xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_When_Profile_Element_Has_No_Name_Attribute()
        {
            // Arrange
            string profileName = "TestProfile";
            string xml = "<Profile><Resource name=\"Resource1\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.XmlProfileNameMatches(profileName, xml);

            // Assert
            result.Should().BeFalse();
        }
    }

    [TestFixture]
    public class IsValidProfileXmlTests
    {
        [Test]
        public void Should_Return_True_For_Valid_Profile_Xml()
        {
            // Arrange
            string xml =
                "<Profile name=\"TestProfile\"><Resource name=\"School\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.ValidateProfileXml(xml).IsValid;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_For_Malformed_Xml()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\">";

            // Act
            bool result = ProfileValidationUtils.ValidateProfileXml(xml).IsValid;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_For_Xml_That_Does_Not_Match_Schema()
        {
            // Arrange - Missing required name attribute on Profile
            string xml = "<Profile><Resource name=\"School\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.ValidateProfileXml(xml).IsValid;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_True_For_Complex_Valid_Profile()
        {
            // Arrange
            string xml =
                @"<Profile name=""TestProfile"">
                <Resource name=""School"">
                    <ReadContentType memberSelection=""ExcludeOnly"">
                        <Property name=""NameOfInstitution"" />
                        <Property name=""SchoolType"" />
                        <Collection name=""SchoolCategories"" memberSelection=""IncludeAll"" />
                    </ReadContentType>
                </Resource>
            </Profile>";

            // Act
            bool result = ProfileValidationUtils.ValidateProfileXml(xml).IsValid;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_For_Empty_String()
        {
            // Arrange
            string xml = "";

            // Act
            bool result = ProfileValidationUtils.ValidateProfileXml(xml).IsValid;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Provide_Detailed_Error_Message_For_Malformed_Xml()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\">";

            // Act
            var validationResult = ProfileValidationUtils.ValidateProfileXml(xml);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Should().NotBeEmpty();
            // For malformed XML, we get XML parsing errors with detailed messages
            validationResult
                .Errors.Should()
                .Contain(error =>
                    error.StartsWith("XML parsing error: ")
                    && error.Length > "XML parsing error: ".Length
                    && error.Contains("Line") // XML parsing errors often include line info
                );
        }

        [Test]
        public void Should_Provide_Detailed_Error_Message_For_Schema_Violation()
        {
            // Arrange - Missing required name attribute on Profile
            string xml = "<Profile><Resource name=\"School\"></Resource></Profile>";

            // Act
            var validationResult = ProfileValidationUtils.ValidateProfileXml(xml);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Should().NotBeEmpty();
            validationResult
                .Errors.Should()
                .Contain(error =>
                    error.StartsWith("Line 1, Column 2: The required attribute 'name' is missing.")
                );
        }
    }

    [TestFixture]
    public class HasAtLeastOneResourceTests
    {
        [Test]
        public void Should_Return_True_When_Profile_Has_One_Resource()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.HasAtLeastOneResource(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_True_When_Profile_Has_Multiple_Resources()
        {
            // Arrange
            string xml =
                "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource name=\"Student\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.HasAtLeastOneResource(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_When_Profile_Has_No_Resources()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"></Profile>";

            // Act
            bool result = ProfileValidationUtils.HasAtLeastOneResource(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_For_Invalid_Xml()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\">";

            // Act
            bool result = ProfileValidationUtils.HasAtLeastOneResource(xml);

            // Assert
            result.Should().BeFalse();
        }
    }

    [TestFixture]
    public class AllResourcesHaveNameAttributeTests
    {
        [Test]
        public void Should_Return_True_When_All_Resources_Have_Name_Attribute()
        {
            // Arrange
            string xml =
                "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource name=\"Student\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.AllResourcesHaveNameAttribute(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_When_One_Resource_Missing_Name_Attribute()
        {
            // Arrange
            string xml =
                "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.AllResourcesHaveNameAttribute(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_When_Resource_Has_Empty_Name_Attribute()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.AllResourcesHaveNameAttribute(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_When_Resource_Has_Whitespace_Name_Attribute()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"   \"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.AllResourcesHaveNameAttribute(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_For_Invalid_Xml()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\">";

            // Act
            bool result = ProfileValidationUtils.AllResourcesHaveNameAttribute(xml);

            // Assert
            result.Should().BeFalse();
        }
    }

    // Small, bounded declarations with an actual entity reference. Name expands to "PPPP".
    public const string NestedEntityXml = """
        <!DOCTYPE Profile [
        <!ENTITY a "P">
        <!ENTITY b "&a;&a;">
        <!ENTITY c "&b;&b;">
        ]>
        <Profile name="&c;"><Resource name="School" /></Profile>
        """;

    [TestFixture("empty internal subset", "P", "<!DOCTYPE Profile []>")]
    [TestFixture("no internal subset", "P", "<!DOCTYPE Profile>")]
    [TestFixture("nested internal entities", "PPPP", NestedEntityXml)]
    [TestFixture("external SYSTEM", "P", "<!DOCTYPE Profile SYSTEM \"urn:dms-1485:external\">")]
    [TestFixture("external PUBLIC", "P", "<!DOCTYPE Profile PUBLIC \"dms-1485\" \"urn:dms-1485:external\">")]
    public class Given_profile_xml_with_a_dtd(string description, string profileName, string declaration)
    {
        private bool _nameMatches;
        private bool _hasResource;
        private bool _allResourcesNamed;

        [SetUp]
        public void Setup()
        {
            // The nested case already carries its own Profile element.
            var xml = declaration.Contains("<Profile", StringComparison.Ordinal)
                ? declaration
                : $"""{declaration}<Profile name="P"><Resource name="School" /></Profile>""";

            _nameMatches = ProfileValidationUtils.XmlProfileNameMatches(profileName, xml);
            _hasResource = ProfileValidationUtils.HasAtLeastOneResource(xml);
            _allResourcesNamed = ProfileValidationUtils.AllResourcesHaveNameAttribute(xml);
        }

        [Test]
        public void It_rejects_the_profile_name()
        {
            _nameMatches.Should().BeFalse(description);
        }

        [Test]
        public void It_rejects_resource_presence()
        {
            _hasResource.Should().BeFalse(description);
        }

        [Test]
        public void It_rejects_resource_names()
        {
            _allResourcesNamed.Should().BeFalse(description);
        }
    }

    [TestFixture("literal space", "A B", "A B")]
    [TestFixture("literal tab", "A\tB", "A\tB")]
    [TestFixture("literal LF", "A\nB", "A\nB")]
    [TestFixture("literal CR", "A\rB", "A\rB")]
    [TestFixture("literal CRLF", "A\r\nB", "A\r\nB")]
    [TestFixture("numeric tab", "A&#9;B", "A\tB")]
    [TestFixture("numeric CR", "A&#13;B", "A\rB")]
    [TestFixture("numeric LF", "A&#10;B", "A\nB")]
    public class Given_profile_xml_attribute_whitespace(
        string description,
        string attributeText,
        string decodedName
    )
    {
        private bool _matchesDecodedName;
        private bool _matchesSpaceName;

        [SetUp]
        public void Setup()
        {
            var xml = $"""<Profile name="{attributeText}"><Resource name="School" /></Profile>""";

            _matchesDecodedName = ProfileValidationUtils.XmlProfileNameMatches(decodedName, xml);
            _matchesSpaceName = ProfileValidationUtils.XmlProfileNameMatches("A B", xml);
        }

        [Test]
        public void It_preserves_exact_name_matching()
        {
            _matchesDecodedName.Should().BeTrue(description);
        }

        [Test]
        public void It_only_matches_the_space_name_when_the_value_is_a_space()
        {
            _matchesSpaceName.Should().Be(decodedName == "A B", description);
        }
    }

    [TestFixture]
    public class Given_profile_xml_without_a_dtd
    {
        private const string Normal = """<Profile name="P"><Resource name="School" /></Profile>""";

        private const string Complex = """
            <Profile name="P">
                <Resource name="School">
                    <ReadContentType memberSelection="ExcludeOnly">
                        <Property name="NameOfInstitution" />
                        <Collection name="SchoolCategories" memberSelection="IncludeAll" />
                    </ReadContentType>
                </Resource>
                <Resource name="Student" />
            </Profile>
            """;

        [Test]
        public void It_accepts_ordinary_and_complex_profiles()
        {
            foreach (var xml in new[] { Normal, Complex })
            {
                ProfileValidationUtils.XmlProfileNameMatches("P", xml).Should().BeTrue();
                ProfileValidationUtils.HasAtLeastOneResource(xml).Should().BeTrue();
                ProfileValidationUtils.AllResourcesHaveNameAttribute(xml).Should().BeTrue();
            }
        }

        [Test]
        public void It_decodes_predefined_entities_and_numeric_references()
        {
            const string xml =
                """<Profile name="A&amp;B &lt;&gt; &quot;&apos; &#x41;&#66;"><Resource name="S&#99;hool" /></Profile>""";

            ProfileValidationUtils.XmlProfileNameMatches("A&B <> \"' AB", xml).Should().BeTrue();
            ProfileValidationUtils.XmlProfileNameMatches("A&amp;B", xml).Should().BeFalse();
            ProfileValidationUtils.HasAtLeastOneResource(xml).Should().BeTrue();
            ProfileValidationUtils.AllResourcesHaveNameAttribute(xml).Should().BeTrue();
        }

        [Test]
        public void It_returns_false_for_malformed_xml()
        {
            const string xml = """<Profile name="P"><Resource name="School">""";

            ProfileValidationUtils.XmlProfileNameMatches("P", xml).Should().BeFalse();
            ProfileValidationUtils.HasAtLeastOneResource(xml).Should().BeFalse();
            ProfileValidationUtils.AllResourcesHaveNameAttribute(xml).Should().BeFalse();
        }

        [Test]
        public void It_keeps_name_matching_case_sensitive_and_root_specific()
        {
            ProfileValidationUtils.XmlProfileNameMatches("p", Normal).Should().BeFalse();
            ProfileValidationUtils
                .XmlProfileNameMatches("P", """<profile name="P"><Resource name="School" /></profile>""")
                .Should()
                .BeFalse();
            ProfileValidationUtils
                .XmlProfileNameMatches("P", """<Other name="P"><Resource name="School" /></Other>""")
                .Should()
                .BeFalse();
        }

        [Test]
        public void It_rejects_missing_empty_and_whitespace_resource_names()
        {
            foreach (
                var resource in new[]
                {
                    "<Resource />",
                    """<Resource name="" />""",
                    """<Resource name="   " />""",
                    """<Resource name="&#32;" />""",
                    """<Resource name="&#9;" />""",
                }
            )
            {
                var xml = $"""<Profile name="P"><Resource name="School" />{resource}</Profile>""";

                ProfileValidationUtils.AllResourcesHaveNameAttribute(xml).Should().BeFalse(resource);
                ProfileValidationUtils.HasAtLeastOneResource(xml).Should().BeTrue(resource);
            }
        }

        [Test]
        public void It_keeps_resource_predicates_independent_of_the_root_element()
        {
            const string otherRoot = """<Other><Resource name="School" /></Other>""";

            ProfileValidationUtils.XmlProfileNameMatches("P", otherRoot).Should().BeFalse();
            ProfileValidationUtils.HasAtLeastOneResource(otherRoot).Should().BeTrue();
            ProfileValidationUtils.AllResourcesHaveNameAttribute(otherRoot).Should().BeTrue();
        }

        [Test]
        public void It_treats_zero_resources_as_absent_but_vacuously_named()
        {
            foreach (var xml in new[] { """<Profile name="P" />""", "<Other />" })
            {
                ProfileValidationUtils.HasAtLeastOneResource(xml).Should().BeFalse(xml);
                ProfileValidationUtils.AllResourcesHaveNameAttribute(xml).Should().BeTrue(xml);
            }
        }

        [Test]
        public void It_accepts_dtd_like_text_in_comments_and_cdata()
        {
            const string xml = """
                <Profile name="P"><!-- <!DOCTYPE Profile [ <!ENTITY a "x"> ]> --><Resource name="School" /><![CDATA[<!DOCTYPE Profile>]]></Profile>
                """;

            ProfileValidationUtils.XmlProfileNameMatches("P", xml).Should().BeTrue();
            ProfileValidationUtils.HasAtLeastOneResource(xml).Should().BeTrue();
            ProfileValidationUtils.AllResourcesHaveNameAttribute(xml).Should().BeTrue();
        }
    }
}
