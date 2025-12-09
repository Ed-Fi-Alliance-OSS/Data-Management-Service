// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileXmlParserTests
{
    [Test]
    public void Parse_ValidXmlWithReadContentType_ShouldParseSuccessfully()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile name=""Test_Profile"">
    <Resource name=""School"">
        <ReadContentType memberSelection=""IncludeOnly"">
            <Property name=""schoolId"" />
            <Property name=""nameOfInstitution"" />
        </ReadContentType>
    </Resource>
</Profile>";

        // Act
        var result = ProfileXmlParser.Parse(xml, "Test description");

        // Assert
        result.Should().NotBeNull();
        result.ProfileName.Should().Be("Test_Profile");
        result.Description.Should().Be("Test description");
        result.ResourcePolicies.Should().HaveCount(1);

        var resourcePolicy = result.ResourcePolicies[0];
        resourcePolicy.ResourceName.Should().Be("School");
        resourcePolicy.ReadContentType.Should().NotBeNull();
        resourcePolicy.ReadContentType!.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        resourcePolicy.ReadContentType.IncludedProperties.Should().Contain("schoolId");
        resourcePolicy.ReadContentType.IncludedProperties.Should().Contain("nameOfInstitution");
        resourcePolicy.WriteContentType.Should().BeNull();
    }

    [Test]
    public void Parse_ValidXmlWithWriteContentType_ShouldParseSuccessfully()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile name=""Write_Profile"">
    <Resource name=""Student"">
        <WriteContentType memberSelection=""IncludeOnly"">
            <Property name=""studentUniqueId"" />
            <Property name=""firstName"" />
            <Property name=""lastSurname"" />
        </WriteContentType>
    </Resource>
</Profile>";

        // Act
        var result = ProfileXmlParser.Parse(xml, null);

        // Assert
        result.Should().NotBeNull();
        result.ProfileName.Should().Be("Write_Profile");
        result.Description.Should().BeNull();
        result.ResourcePolicies.Should().HaveCount(1);

        var resourcePolicy = result.ResourcePolicies[0];
        resourcePolicy.ResourceName.Should().Be("Student");
        resourcePolicy.ReadContentType.Should().BeNull();
        resourcePolicy.WriteContentType.Should().NotBeNull();
        resourcePolicy.WriteContentType!.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        resourcePolicy.WriteContentType.IncludedProperties.Should().HaveCount(3);
    }

    [Test]
    public void Parse_ValidXmlWithBothReadAndWrite_ShouldParseSuccessfully()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile name=""Full_Profile"">
    <Resource name=""School"">
        <ReadContentType memberSelection=""IncludeOnly"">
            <Property name=""schoolId"" />
        </ReadContentType>
        <WriteContentType memberSelection=""IncludeOnly"">
            <Property name=""schoolId"" />
            <Property name=""nameOfInstitution"" />
        </WriteContentType>
    </Resource>
</Profile>";

        // Act
        var result = ProfileXmlParser.Parse(xml, null);

        // Assert
        result.Should().NotBeNull();
        result.ResourcePolicies.Should().HaveCount(1);

        var resourcePolicy = result.ResourcePolicies[0];
        resourcePolicy.ReadContentType.Should().NotBeNull();
        resourcePolicy.WriteContentType.Should().NotBeNull();
        resourcePolicy.ReadContentType!.IncludedProperties.Should().HaveCount(1);
        resourcePolicy.WriteContentType!.IncludedProperties.Should().HaveCount(2);
    }

    [Test]
    public void Parse_MultipleResources_ShouldParseAllResources()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile name=""Multi_Profile"">
    <Resource name=""School"">
        <ReadContentType memberSelection=""IncludeOnly"">
            <Property name=""schoolId"" />
        </ReadContentType>
    </Resource>
    <Resource name=""Student"">
        <ReadContentType memberSelection=""IncludeOnly"">
            <Property name=""studentUniqueId"" />
        </ReadContentType>
    </Resource>
</Profile>";

        // Act
        var result = ProfileXmlParser.Parse(xml, null);

        // Assert
        result.Should().NotBeNull();
        result.ResourcePolicies.Should().HaveCount(2);
        result.ResourcePolicies.Should().Contain(rp => rp.ResourceName == "School");
        result.ResourcePolicies.Should().Contain(rp => rp.ResourceName == "Student");
    }

    [Test]
    public void Parse_MissingProfileNameAttribute_ShouldThrowException()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile>
    <Resource name=""School"">
        <ReadContentType memberSelection=""IncludeOnly"">
            <Property name=""schoolId"" />
        </ReadContentType>
    </Resource>
</Profile>";

        // Act & Assert
        var act = () => ProfileXmlParser.Parse(xml, null);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Profile name attribute is required*");
    }

    [Test]
    public void Parse_MissingResourceNameAttribute_ShouldThrowException()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile name=""Test_Profile"">
    <Resource>
        <ReadContentType memberSelection=""IncludeOnly"">
            <Property name=""schoolId"" />
        </ReadContentType>
    </Resource>
</Profile>";

        // Act & Assert
        var act = () => ProfileXmlParser.Parse(xml, null);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Resource name attribute is required*");
    }

    [Test]
    public void Parse_MissingProfileElement_ShouldThrowException()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<NotAProfile name=""Test"">
</NotAProfile>";

        // Act & Assert
        var act = () => ProfileXmlParser.Parse(xml, null);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Root Profile element not found*");
    }

    [Test]
    public void Parse_InvalidXml_ShouldThrowException()
    {
        // Arrange
        var xml = "This is not valid XML";

        // Act & Assert
        var act = () => ProfileXmlParser.Parse(xml, null);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to parse profile XML*");
    }

    [Test]
    public void Parse_DefaultMemberSelection_ShouldBeIncludeAll()
    {
        // Arrange
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile name=""Default_Profile"">
    <Resource name=""School"">
        <ReadContentType>
            <Property name=""schoolId"" />
        </ReadContentType>
    </Resource>
</Profile>";

        // Act
        var result = ProfileXmlParser.Parse(xml, null);

        // Assert
        result.Should().NotBeNull();
        result.ResourcePolicies[0].ReadContentType!.MemberSelection
            .Should().Be(MemberSelection.IncludeAll);
    }
}
