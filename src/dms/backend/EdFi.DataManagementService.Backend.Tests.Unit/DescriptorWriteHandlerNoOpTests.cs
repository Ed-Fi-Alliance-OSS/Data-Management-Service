// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_No_Op_Comparison
{
    [TestCase("uri://ED-FI.org/SchoolTypeDescriptor", "Part#Charter")]
    [TestCase("uri://ed-fi.org/SchoolTypeDescriptor", "PART#Charter")]
    [TestCase("uri://ed-fi.org/SchoolTypeDescriptor#Part", "Charter")]
    public void It_compares_each_stored_component_ordinally_even_when_URI_identity_matches(
        string @namespace,
        string codeValue
    )
    {
        var original = new ExtractedDescriptorBody(
            "uri://ed-fi.org/SchoolTypeDescriptor",
            "Part#Charter",
            "Charter",
            null,
            null,
            null
        );
        var changed = original with { Namespace = @namespace, CodeValue = codeValue };

        DescriptorNoOpComparer.IsUnchanged(changed, original).Should().BeFalse();
    }

    [Test]
    public void It_detects_identical_extracted_bodies_as_unchanged()
    {
        var a = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "English Language Arts",
            new DateOnly(2024, 1, 1),
            null
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "English Language Arts",
            new DateOnly(2024, 1, 1),
            null
        );

        DescriptorNoOpComparer.IsUnchanged(a, b).Should().BeTrue();
    }

    [Test]
    public void It_detects_changed_description_as_different()
    {
        var a = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "Old Description",
            null,
            null
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "New Description",
            null,
            null
        );

        DescriptorNoOpComparer.IsUnchanged(a, b).Should().BeFalse();
    }

    [Test]
    public void It_detects_changed_effective_dates_as_different()
    {
        var a = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            new DateOnly(2024, 1, 1),
            null
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            new DateOnly(2025, 1, 1),
            null
        );

        DescriptorNoOpComparer.IsUnchanged(a, b).Should().BeFalse();
    }

    [Test]
    public void It_detects_null_vs_non_null_as_different()
    {
        var a = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            null,
            null,
            null,
            null
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            null,
            null
        );

        DescriptorNoOpComparer.IsUnchanged(a, b).Should().BeFalse();
    }

    [Test]
    public void It_detects_identity_change_via_namespace_and_code_value_difference()
    {
        var original = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            null,
            null
        );

        var changed = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "Mathematics",
            "Mathematics",
            null,
            null,
            null
        );

        DescriptorNoOpComparer.IsUnchanged(original, changed).Should().BeFalse();
    }
}
