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
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.IsValidProfileXml(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_For_Malformed_Xml()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\">";

            // Act
            bool result = ProfileValidationUtils.IsValidProfileXml(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_For_Xml_That_Does_Not_Match_Schema()
        {
            // Arrange - Missing required name attribute on Profile
            string xml = "<Profile><Resource name=\"School\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.IsValidProfileXml(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_True_For_Complex_Valid_Profile()
        {
            // Arrange
            string xml = @"<Profile name=""TestProfile"">
                <Resource name=""School"">
                    <ReadContentType memberSelection=""ExcludeOnly"">
                        <Property name=""NameOfInstitution"" />
                        <Property name=""SchoolType"" />
                        <Collection name=""SchoolCategories"" memberSelection=""IncludeAll"" />
                    </ReadContentType>
                </Resource>
            </Profile>";

            // Act
            bool result = ProfileValidationUtils.IsValidProfileXml(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_For_Empty_String()
        {
            // Arrange
            string xml = "";

            // Act
            bool result = ProfileValidationUtils.IsValidProfileXml(xml);

            // Assert
            result.Should().BeFalse();
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
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource name=\"Student\"></Resource></Profile>";

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
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource name=\"Student\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.AllResourcesHaveNameAttribute(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_When_One_Resource_Missing_Name_Attribute()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource></Resource></Profile>";

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

    [TestFixture]
    public class NoDuplicateResourceNamesTests
    {
        [Test]
        public void Should_Return_True_When_All_Resource_Names_Are_Unique()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource name=\"Student\"></Resource><Resource name=\"Staff\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.NoDuplicateResourceNames(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_When_Resource_Names_Are_Duplicated()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource name=\"Student\"></Resource><Resource name=\"School\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.NoDuplicateResourceNames(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_False_When_Resource_Names_Are_Duplicated_Case_Insensitive()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource><Resource name=\"SCHOOL\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.NoDuplicateResourceNames(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Return_True_When_Only_One_Resource_Exists()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.NoDuplicateResourceNames(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_True_When_No_Resources_Exist()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"></Profile>";

            // Act
            bool result = ProfileValidationUtils.NoDuplicateResourceNames(xml);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Should_Return_False_For_Invalid_Xml()
        {
            // Arrange
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"School\">";

            // Act
            bool result = ProfileValidationUtils.NoDuplicateResourceNames(xml);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Should_Ignore_Resources_With_Empty_Names()
        {
            // Arrange - Resources with empty names should be ignored in duplicate check
            string xml = "<Profile name=\"TestProfile\"><Resource name=\"\"></Resource><Resource name=\"\"></Resource><Resource name=\"School\"></Resource></Profile>";

            // Act
            bool result = ProfileValidationUtils.NoDuplicateResourceNames(xml);

            // Assert
            result.Should().BeTrue();
        }
    }
}
