// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Write_Body_Extractor
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "AcademicSubjectDescriptor");

    [Test]
    public void It_extracts_all_fields_from_a_complete_descriptor_body()
    {
        var body = JsonNode.Parse(
            """
            {
                "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                "codeValue": "English",
                "shortDescription": "English",
                "description": "English Language Arts",
                "effectiveBeginDate": "2024-01-01",
                "effectiveEndDate": "2025-12-31"
            }
            """
        )!;

        var result = DescriptorWriteBodyExtractor.Extract(body, _resource);

        result.Namespace.Should().Be("uri://ed-fi.org/AcademicSubjectDescriptor");
        result.CodeValue.Should().Be("English");
        result.ShortDescription.Should().Be("English");
        result.Description.Should().Be("English Language Arts");
        result.EffectiveBeginDate.Should().Be(new DateOnly(2024, 1, 1));
        result.EffectiveEndDate.Should().Be(new DateOnly(2025, 12, 31));
    }

    [Test]
    public void It_computes_uri_as_namespace_hash_codeValue()
    {
        var body = JsonNode.Parse(
            """
            {
                "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                "codeValue": "English"
            }
            """
        )!;

        var result = DescriptorWriteBodyExtractor.Extract(body, _resource);

        result.Uri.Should().Be("uri://ed-fi.org/AcademicSubjectDescriptor#English");
    }

    [Test]
    public void It_derives_discriminator_from_resource_name()
    {
        var body = JsonNode.Parse(
            """
            {
                "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                "codeValue": "English"
            }
            """
        )!;

        var result = DescriptorWriteBodyExtractor.Extract(body, _resource);

        result.Discriminator.Should().Be("AcademicSubjectDescriptor");
    }

    [Test]
    public void It_handles_missing_optional_fields()
    {
        var body = JsonNode.Parse(
            """
            {
                "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                "codeValue": "English"
            }
            """
        )!;

        var result = DescriptorWriteBodyExtractor.Extract(body, _resource);

        result.ShortDescription.Should().BeNull();
        result.Description.Should().BeNull();
        result.EffectiveBeginDate.Should().BeNull();
        result.EffectiveEndDate.Should().BeNull();
    }

    [Test]
    public void It_preserves_original_case_in_uri()
    {
        var body = JsonNode.Parse(
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Alternative"
            }
            """
        )!;

        var resource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var result = DescriptorWriteBodyExtractor.Extract(body, resource);

        result.Uri.Should().Be("uri://ed-fi.org/SchoolTypeDescriptor#Alternative");
    }

    [Test]
    public void It_throws_when_namespace_is_missing()
    {
        var body = JsonNode.Parse(
            """
            {
                "codeValue": "English"
            }
            """
        )!;

        var act = () => DescriptorWriteBodyExtractor.Extract(body, _resource);

        act.Should().Throw<InvalidOperationException>().WithMessage("*missing required field 'namespace'*");
    }

    [Test]
    public void It_throws_when_codeValue_is_missing()
    {
        var body = JsonNode.Parse(
            """
            {
                "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor"
            }
            """
        )!;

        var act = () => DescriptorWriteBodyExtractor.Extract(body, _resource);

        act.Should().Throw<InvalidOperationException>().WithMessage("*missing required field 'codeValue'*");
    }
}
