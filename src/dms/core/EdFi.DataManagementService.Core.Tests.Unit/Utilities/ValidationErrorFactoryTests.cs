// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

[TestFixture]
public class ValidationErrorFactoryTests
{
    private static readonly BaseResourceInfo _documentTargetResource = new(
        new ProjectName("ed-fi"),
        new ResourceName("School"),
        false
    );

    private static readonly BaseResourceInfo _descriptorTargetResource = new(
        new ProjectName("ed-fi"),
        new ResourceName("SchoolTypeDescriptor"),
        true
    );

    private static DocumentReferenceFailure CreateMissingDocumentReferenceFailure(string path) =>
        new(
            Path: new JsonPath(path),
            TargetResource: _documentTargetResource,
            DocumentIdentity: new([]),
            ReferentialId: new ReferentialId(Guid.NewGuid()),
            Reason: DocumentReferenceFailureReason.Missing
        );

    private static DescriptorReferenceFailure CreateDescriptorReferenceFailure(
        string path,
        DescriptorReferenceFailureReason reason,
        string descriptorValue = "uri://ed-fi.org/schooltypedescriptor#elementary"
    ) =>
        new(
            Path: new JsonPath(path),
            TargetResource: _descriptorTargetResource,
            DocumentIdentity: new([new(DocumentIdentity.DescriptorIdentityJsonPath, descriptorValue)]),
            ReferentialId: new ReferentialId(Guid.NewGuid()),
            Reason: reason
        );

    [TestFixture]
    public class Given_Mixed_Reference_Failures_With_A_Path_Collision : ValidationErrorFactoryTests
    {
        private Dictionary<string, string[]> _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ValidationErrorFactory.BuildInvalidWriteReferenceValidationErrors(
                [CreateMissingDocumentReferenceFailure("$.sharedReference")],
                [
                    CreateDescriptorReferenceFailure(
                        "$.sharedReference",
                        DescriptorReferenceFailureReason.DescriptorTypeMismatch,
                        "uri://ed-fi.org/gradeleveldescriptor#first-grade"
                    ),
                ]
            );
        }

        [Test]
        public void It_merges_both_messages_under_the_same_path()
        {
            _result.Keys.Should().Equal("$.sharedReference");
            _result["$.sharedReference"]
                .Should()
                .Equal(
                    "The referenced School item does not exist.",
                    "SchoolTypeDescriptor value 'uri://ed-fi.org/gradeleveldescriptor#first-grade' is not a valid SchoolTypeDescriptor."
                );
        }
    }

    [TestFixture]
    public class Given_Document_Only_Reference_Failures : ValidationErrorFactoryTests
    {
        private Dictionary<string, string[]> _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ValidationErrorFactory.BuildInvalidWriteReferenceValidationErrors(
                [CreateMissingDocumentReferenceFailure("$.schoolReference")],
                []
            );
        }

        [Test]
        public void It_returns_only_document_reference_messages()
        {
            _result.Keys.Should().Equal("$.schoolReference");
            _result["$.schoolReference"].Should().Equal("The referenced School item does not exist.");
        }
    }

    [TestFixture]
    public class Given_Descriptor_Only_Reference_Failures : ValidationErrorFactoryTests
    {
        private Dictionary<string, string[]> _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ValidationErrorFactory.BuildInvalidWriteReferenceValidationErrors(
                [],
                [
                    CreateDescriptorReferenceFailure(
                        "$.schoolTypeDescriptor",
                        DescriptorReferenceFailureReason.Missing
                    ),
                ]
            );
        }

        [Test]
        public void It_returns_only_descriptor_reference_messages()
        {
            _result.Keys.Should().Equal("$.schoolTypeDescriptor");
            _result["$.schoolTypeDescriptor"]
                .Should()
                .Equal(
                    "SchoolTypeDescriptor value 'uri://ed-fi.org/schooltypedescriptor#elementary' does not exist."
                );
        }
    }

    [TestFixture]
    public class Given_Mixed_Reference_Failures_At_Different_Paths : ValidationErrorFactoryTests
    {
        private Dictionary<string, string[]> _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ValidationErrorFactory.BuildInvalidWriteReferenceValidationErrors(
                [
                    CreateMissingDocumentReferenceFailure("$.schoolReference"),
                    CreateMissingDocumentReferenceFailure("$.sessionReference.schoolReference"),
                ],
                [
                    CreateDescriptorReferenceFailure(
                        "$.schoolTypeDescriptor",
                        DescriptorReferenceFailureReason.Missing
                    ),
                ]
            );
        }

        [Test]
        public void It_keeps_separate_paths_and_messages()
        {
            _result
                .Keys.Should()
                .Equal("$.schoolReference", "$.sessionReference.schoolReference", "$.schoolTypeDescriptor");
            _result["$.schoolReference"].Should().Equal("The referenced School item does not exist.");
            _result["$.sessionReference.schoolReference"]
                .Should()
                .Equal("The referenced School item does not exist.");
            _result["$.schoolTypeDescriptor"]
                .Should()
                .Equal(
                    "SchoolTypeDescriptor value 'uri://ed-fi.org/schooltypedescriptor#elementary' does not exist."
                );
        }
    }
}
