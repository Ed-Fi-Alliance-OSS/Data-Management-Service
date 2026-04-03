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
    [Test]
    public void It_detects_identical_extracted_bodies_as_unchanged()
    {
        var a = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "English Language Arts",
            new DateOnly(2024, 1, 1),
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "English Language Arts",
            new DateOnly(2024, 1, 1),
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        // The no-op comparison in DescriptorWriteHandler compares ShortDescription,
        // Description, EffectiveBeginDate, EffectiveEndDate. Records give us
        // value equality on all fields, which is a superset of the no-op check.
        a.Should().Be(b);
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
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "New Description",
            null,
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        a.Should().NotBe(b);
        a.Description.Should().NotBe(b.Description);
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
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            new DateOnly(2025, 1, 1),
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        a.EffectiveBeginDate.Should().NotBe(b.EffectiveBeginDate);
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
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        var b = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            null,
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        a.ShortDescription.Should().NotBe(b.ShortDescription);
    }

    [Test]
    public void It_detects_identity_change_via_uri_difference()
    {
        var original = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            null,
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        var changed = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "Mathematics",
            "Mathematics",
            null,
            null,
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#Mathematics",
            "AcademicSubjectDescriptor"
        );

        original.Uri.Should().NotBe(changed.Uri);
    }
}
