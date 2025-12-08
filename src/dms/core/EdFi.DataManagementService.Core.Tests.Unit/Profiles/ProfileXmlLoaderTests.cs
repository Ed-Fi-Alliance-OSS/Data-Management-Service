// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profiles;
using EdFi.DataManagementService.Core.Profiles.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profiles;

[TestFixture]
[Parallelizable]
public class ProfileXmlLoaderTests
{
    private ProfileXmlLoader _loader = null!;
    private string _testProfilesPath = null!;

    [SetUp]
    public void Setup()
    {
        _loader = new ProfileXmlLoader(NullLogger<ProfileXmlLoader>.Instance);
        _testProfilesPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Profiles",
            "TestProfiles"
        );
    }

    [TestFixture]
    [Parallelizable]
    public class LoadProfilesFromDirectory : ProfileXmlLoaderTests
    {
        [Test]
        public void Should_load_all_three_profiles()
        {
            // Act
            var profiles = _loader.LoadProfilesFromDirectory(_testProfilesPath);

            // Assert
            profiles.Should().HaveCount(3);
            profiles.Select(p => p.Name).Should().Contain(new[]
            {
                "Student-Exclude-BirthDate",
                "Test-Profile-Resource-ExcludeOnly",
                "Test-Profile-Resource-IncludeOnly"
            });
        }

        [Test]
        public void Should_return_empty_array_for_nonexistent_directory()
        {
            // Act
            var profiles = _loader.LoadProfilesFromDirectory("/nonexistent/path");

            // Assert
            profiles.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class ParseStudentExcludeBirthDate : ProfileXmlLoaderTests
    {
        private ApiProfile _profile = null!;

        [SetUp]
        public void LoadProfile()
        {
            var xmlPath = Path.Combine(_testProfilesPath, "Student-Exclude-BirthDate.xml");
            _profile = _loader.LoadProfileFromFile(xmlPath)!;
        }

        [Test]
        public void Should_have_correct_profile_name()
        {
            _profile.Name.Should().Be("Student-Exclude-BirthDate");
        }

        [Test]
        public void Should_have_one_resource()
        {
            _profile.Resources.Should().HaveCount(1);
        }

        [Test]
        public void Should_have_student_resource()
        {
            _profile.Resources[0].Name.Should().Be("Student");
        }

        [Test]
        public void Should_have_read_content_type()
        {
            var resource = _profile.Resources[0];
            resource.ReadContentType.Should().NotBeNull();
            resource.ReadContentType!.MemberSelection.Should().Be(MemberSelection.ExcludeOnly);
        }

        [Test]
        public void Should_have_write_content_type()
        {
            var resource = _profile.Resources[0];
            resource.WriteContentType.Should().NotBeNull();
            resource.WriteContentType!.MemberSelection.Should().Be(MemberSelection.ExcludeOnly);
        }

        [Test]
        public void Should_exclude_birthdate_property_in_read()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.Properties.Should().HaveCount(1);
            readContentType.Properties[0].Name.Should().Be("BirthDate");
        }

        [Test]
        public void Should_exclude_birthdate_property_in_write()
        {
            var writeContentType = _profile.Resources[0].WriteContentType!;
            writeContentType.Properties.Should().HaveCount(1);
            writeContentType.Properties[0].Name.Should().Be("BirthDate");
        }

        [Test]
        public void Should_have_no_collections()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.Collections.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class ParseTestProfileResourceExcludeOnly : ProfileXmlLoaderTests
    {
        private ApiProfile _profile = null!;

        [SetUp]
        public void LoadProfile()
        {
            var xmlPath = Path.Combine(_testProfilesPath, "Test-Profile-Resource-ExcludeOnly.xml");
            _profile = _loader.LoadProfileFromFile(xmlPath)!;
        }

        [Test]
        public void Should_have_correct_profile_name()
        {
            _profile.Name.Should().Be("Test-Profile-Resource-ExcludeOnly");
        }

        [Test]
        public void Should_have_school_resource()
        {
            _profile.Resources[0].Name.Should().Be("School");
        }

        [Test]
        public void Should_have_exclude_only_read_member_selection()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.MemberSelection.Should().Be(MemberSelection.ExcludeOnly);
        }

        [Test]
        public void Should_have_five_excluded_properties_in_read()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.Properties.Should().HaveCount(5);
            readContentType.Properties.Select(p => p.Name).Should().Contain(new[]
            {
                "NameOfInstitution",
                "OperationalStatusDescriptor",
                "CharterApprovalSchoolYearTypeReference",
                "SchoolTypeDescriptor",
                "AdministrativeFundingControlDescriptor"
            });
        }

        [Test]
        public void Should_have_two_collections_in_read()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.Collections.Should().HaveCount(2);
            readContentType.Collections.Select(c => c.Name).Should().Contain(new[]
            {
                "EducationOrganizationAddresses",
                "SchoolCategories"
            });
        }

        [Test]
        public void Should_have_include_all_for_collections()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.Collections.Should().AllSatisfy(c =>
                c.MemberSelection.Should().Be(MemberSelection.IncludeAll)
            );
        }

        [Test]
        public void Should_have_exclude_only_write_member_selection()
        {
            var writeContentType = _profile.Resources[0].WriteContentType!;
            writeContentType.MemberSelection.Should().Be(MemberSelection.ExcludeOnly);
        }

        [Test]
        public void Should_have_five_excluded_properties_in_write()
        {
            var writeContentType = _profile.Resources[0].WriteContentType!;
            writeContentType.Properties.Should().HaveCount(5);
            writeContentType.Properties.Select(p => p.Name).Should().Contain(new[]
            {
                "ShortNameOfInstitution",
                "OperationalStatusDescriptor",
                "WebSite",
                "CharterStatusDescriptor",
                "AdministrativeFundingControlDescriptor"
            });
        }
    }

    [TestFixture]
    [Parallelizable]
    public class ParseTestProfileResourceIncludeOnly : ProfileXmlLoaderTests
    {
        private ApiProfile _profile = null!;

        [SetUp]
        public void LoadProfile()
        {
            var xmlPath = Path.Combine(_testProfilesPath, "Test-Profile-Resource-IncludeOnly.xml");
            _profile = _loader.LoadProfileFromFile(xmlPath)!;
        }

        [Test]
        public void Should_have_correct_profile_name()
        {
            _profile.Name.Should().Be("Test-Profile-Resource-IncludeOnly");
        }

        [Test]
        public void Should_have_include_only_read_member_selection()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void Should_have_five_included_properties_in_read()
        {
            var readContentType = _profile.Resources[0].ReadContentType!;
            readContentType.Properties.Should().HaveCount(5);
            readContentType.Properties.Select(p => p.Name).Should().Contain(new[]
            {
                "NameOfInstitution",
                "OperationalStatusDescriptor",
                "CharterApprovalSchoolYearTypeReference",
                "SchoolTypeDescriptor",
                "AdministrativeFundingControlDescriptor"
            });
        }

        [Test]
        public void Should_have_include_only_write_member_selection()
        {
            var writeContentType = _profile.Resources[0].WriteContentType!;
            writeContentType.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void Should_have_five_included_properties_in_write()
        {
            var writeContentType = _profile.Resources[0].WriteContentType!;
            writeContentType.Properties.Should().HaveCount(5);
            writeContentType.Properties.Select(p => p.Name).Should().Contain(new[]
            {
                "ShortNameOfInstitution",
                "OperationalStatusDescriptor",
                "WebSite",
                "CharterStatusDescriptor",
                "AdministrativeFundingControlDescriptor"
            });
        }
    }

    [TestFixture]
    [Parallelizable]
    public class ParseProfileFromXml : ProfileXmlLoaderTests
    {
        [Test]
        public void Should_parse_xml_string()
        {
            // Arrange
            var xml = @"<?xml version=""1.0"" encoding=""utf-8"" ?>
<Profile name=""TestProfile"">
  <Resource name=""TestResource"">
    <ReadContentType memberSelection=""IncludeOnly"">
      <Property name=""TestProperty"" />
    </ReadContentType>
  </Resource>
</Profile>";

            // Act
            var profile = _loader.ParseProfileFromXml(xml);

            // Assert
            profile.Should().NotBeNull();
            profile!.Name.Should().Be("TestProfile");
            profile.Resources[0].Name.Should().Be("TestResource");
        }

        [Test]
        public void Should_return_null_for_invalid_xml()
        {
            // Arrange
            var xml = @"<NotAProfile name=""Test""></NotAProfile>";

            // Act
            var profile = _loader.ParseProfileFromXml(xml);

            // Assert
            profile.Should().BeNull();
        }
    }
}
