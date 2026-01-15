// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileHeaderParserTests
{
    [TestFixture]
    public class Given_Null_Or_Empty_Header : ProfileHeaderParserTests
    {
        [Test]
        public void It_returns_no_profile_header_for_null()
        {
            var result = ProfileHeaderParser.Parse(null);

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
            result.ErrorMessage.Should().BeNull();
        }

        [Test]
        public void It_returns_no_profile_header_for_empty_string()
        {
            var result = ProfileHeaderParser.Parse("");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }

        [Test]
        public void It_returns_no_profile_header_for_whitespace()
        {
            var result = ProfileHeaderParser.Parse("   ");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Standard_Json_Content_Type : ProfileHeaderParserTests
    {
        [Test]
        public void It_returns_no_profile_header_for_application_json()
        {
            var result = ProfileHeaderParser.Parse("application/json");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }

        [Test]
        public void It_returns_no_profile_header_for_text_json()
        {
            var result = ProfileHeaderParser.Parse("text/json");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }

        [Test]
        public void It_returns_no_profile_header_for_application_json_with_charset()
        {
            var result = ProfileHeaderParser.Parse("application/json; charset=utf-8");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }

        [Test]
        public void It_returns_no_profile_header_for_application_json_uppercase()
        {
            var result = ProfileHeaderParser.Parse("APPLICATION/JSON");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Valid_Profile_Header : ProfileHeaderParserTests
    {
        [Test]
        public void It_parses_readable_profile_header()
        {
            var result = ProfileHeaderParser.Parse(
                "application/vnd.ed-fi.student.test-profile.readable+json"
            );

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().NotBeNull();
            result.ParsedHeader!.ResourceName.Should().Be("student");
            result.ParsedHeader.ProfileName.Should().Be("test-profile");
            result.ParsedHeader.UsageType.Should().Be(ProfileUsageType.Readable);
        }

        [Test]
        public void It_parses_writable_profile_header()
        {
            var result = ProfileHeaderParser.Parse(
                "application/vnd.ed-fi.student.test-profile.writable+json"
            );

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().NotBeNull();
            result.ParsedHeader!.UsageType.Should().Be(ProfileUsageType.Writable);
        }

        [Test]
        public void It_parses_profile_header_case_insensitively()
        {
            var result = ProfileHeaderParser.Parse(
                "APPLICATION/VND.ED-FI.Student.Test-Profile.READABLE+JSON"
            );

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().NotBeNull();
            result.ParsedHeader!.ResourceName.Should().Be("Student");
            result.ParsedHeader.ProfileName.Should().Be("Test-Profile");
            result.ParsedHeader.UsageType.Should().Be(ProfileUsageType.Readable);
        }

        [Test]
        public void It_returns_no_profile_for_header_with_leading_whitespace()
        {
            // Leading whitespace causes the StartsWith check to fail, treating it as non-profile header
            var result = ProfileHeaderParser.Parse("  application/vnd.ed-fi.student.myprofile.readable+json");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }

        [Test]
        public void It_parses_profile_with_numbers_in_resource_name()
        {
            var result = ProfileHeaderParser.Parse("application/vnd.ed-fi.student2.myprofile.readable+json");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader!.ResourceName.Should().Be("student2");
        }

        [Test]
        public void It_parses_profile_with_hyphens_in_profile_name()
        {
            var result = ProfileHeaderParser.Parse(
                "application/vnd.ed-fi.student.my-complex-profile-name.readable+json"
            );

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader!.ProfileName.Should().Be("my-complex-profile-name");
        }
    }

    [TestFixture]
    public class Given_Malformed_Profile_Header : ProfileHeaderParserTests
    {
        [Test]
        public void It_fails_for_missing_usage_type()
        {
            var result = ProfileHeaderParser.Parse("application/vnd.ed-fi.student.test-profile+json");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("invalid");
        }

        [Test]
        public void It_fails_for_invalid_usage_type()
        {
            var result = ProfileHeaderParser.Parse("application/vnd.ed-fi.student.test-profile.invalid+json");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("invalid");
        }

        [Test]
        public void It_fails_for_missing_profile_name()
        {
            var result = ProfileHeaderParser.Parse("application/vnd.ed-fi.student.readable+json");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("invalid");
        }

        [Test]
        public void It_fails_for_hyphen_in_resource_name()
        {
            var result = ProfileHeaderParser.Parse(
                "application/vnd.ed-fi.student-school.profile.readable+json"
            );

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("invalid");
        }

        [Test]
        public void It_fails_for_missing_json_suffix()
        {
            var result = ProfileHeaderParser.Parse("application/vnd.ed-fi.student.test-profile.readable");

            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("invalid");
        }
    }

    [TestFixture]
    public class Given_Non_Profile_Content_Type : ProfileHeaderParserTests
    {
        [Test]
        public void It_returns_no_profile_header_for_xml()
        {
            var result = ProfileHeaderParser.Parse("application/xml");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }

        [Test]
        public void It_returns_no_profile_header_for_html()
        {
            var result = ProfileHeaderParser.Parse("text/html");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }

        [Test]
        public void It_returns_no_profile_header_for_other_vnd_type()
        {
            var result = ProfileHeaderParser.Parse("application/vnd.other.type+json");

            result.IsSuccess.Should().BeTrue();
            result.ParsedHeader.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_BuildProfileContentType : ProfileHeaderParserTests
    {
        [Test]
        public void It_builds_readable_content_type()
        {
            var result = ProfileHeaderParser.BuildProfileContentType(
                "Student",
                "TestProfile",
                ProfileUsageType.Readable
            );

            result.Should().Be("application/vnd.ed-fi.student.testprofile.readable+json");
        }

        [Test]
        public void It_builds_writable_content_type()
        {
            var result = ProfileHeaderParser.BuildProfileContentType(
                "Student",
                "TestProfile",
                ProfileUsageType.Writable
            );

            result.Should().Be("application/vnd.ed-fi.student.testprofile.writable+json");
        }
    }
}
