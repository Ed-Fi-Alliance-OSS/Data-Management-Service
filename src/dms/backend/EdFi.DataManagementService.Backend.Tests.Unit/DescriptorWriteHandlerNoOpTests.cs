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

        DescriptorNoOpComparer.IsUnchanged(original, changed).Should().BeFalse();
    }

    [Test]
    public void It_treats_an_identity_that_differs_only_in_casing_as_unchanged()
    {
        // UX_Descriptor_UriLowered_Discriminator treats case variants as one identity and POST-as-update
        // keeps the stored casing, so a case-only identity difference has nothing to write.
        var persisted = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            "English Language Arts",
            new DateOnly(2024, 1, 1),
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        var caseVariant = new ExtractedDescriptorBody(
            "URI://ED-FI.ORG/AcademicSubjectDescriptor",
            "ENGLISH",
            "English",
            "English Language Arts",
            new DateOnly(2024, 1, 1),
            null,
            "URI://ED-FI.ORG/AcademicSubjectDescriptor#ENGLISH",
            "AcademicSubjectDescriptor"
        );

        DescriptorNoOpComparer.IsUnchanged(caseVariant, persisted).Should().BeTrue();
    }

    [Test]
    public void It_keeps_descriptive_fields_case_sensitive_when_the_identity_matches_case_insensitively()
    {
        // Only the identity fields relax to OrdinalIgnoreCase; a case-only ShortDescription edit is a
        // real content change that must still be written.
        var persisted = new ExtractedDescriptorBody(
            "uri://ed-fi.org/AcademicSubjectDescriptor",
            "English",
            "English",
            null,
            null,
            null,
            "uri://ed-fi.org/AcademicSubjectDescriptor#English",
            "AcademicSubjectDescriptor"
        );

        var caseVariant = new ExtractedDescriptorBody(
            "URI://ED-FI.ORG/AcademicSubjectDescriptor",
            "ENGLISH",
            "ENGLISH",
            null,
            null,
            null,
            "URI://ED-FI.ORG/AcademicSubjectDescriptor#ENGLISH",
            "AcademicSubjectDescriptor"
        );

        DescriptorNoOpComparer.IsUnchanged(caseVariant, persisted).Should().BeFalse();
    }
}
