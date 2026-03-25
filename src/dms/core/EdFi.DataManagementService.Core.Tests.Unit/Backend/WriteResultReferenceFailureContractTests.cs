// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
[Parallelizable]
public class WriteResultReferenceFailureContractTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_An_UpsertFailureReference_With_Repeated_ReferentialIds_At_Different_Paths
        : WriteResultReferenceFailureContractTests
    {
        private UpsertResult.UpsertFailureReference _result = null!;

        [SetUp]
        public void Setup()
        {
            var targetResource = new BaseResourceInfo(
                ProjectName: new ProjectName("ed-fi"),
                ResourceName: new ResourceName("School"),
                IsDescriptor: false
            );
            var sharedReferentialId = new ReferentialId(Guid.NewGuid());
            DocumentIdentity documentIdentity = new([
                new(new JsonPath("$.schoolReference.schoolId"), "255901001"),
            ]);

            _result = new UpsertResult.UpsertFailureReference([
                new(
                    Path: new JsonPath("$.schoolReference"),
                    TargetResource: targetResource,
                    DocumentIdentity: documentIdentity,
                    ReferentialId: sharedReferentialId,
                    Reason: DocumentReferenceFailureReason.Missing
                ),
                new(
                    Path: new JsonPath("$.sessionReference.schoolReference"),
                    TargetResource: targetResource,
                    DocumentIdentity: documentIdentity,
                    ReferentialId: sharedReferentialId,
                    Reason: DocumentReferenceFailureReason.Missing
                ),
            ]);
        }

        [Test]
        public void It_preserves_every_failing_occurrence()
        {
            _result.InvalidDocumentReferences.Should().HaveCount(2);
            _result
                .InvalidDocumentReferences.Select(failure => failure.Path.Value)
                .Should()
                .Equal("$.schoolReference", "$.sessionReference.schoolReference");
            _result
                .InvalidDocumentReferences.Select(failure => failure.Reason)
                .Should()
                .OnlyContain(reason => reason == DocumentReferenceFailureReason.Missing);
        }

        [Test]
        public void It_still_exposes_deduped_resource_names_for_current_handlers()
        {
            _result.GetResourceNames().Should().Equal(new ResourceName("School"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_UpdateFailureReference_With_An_Incompatible_Target_Type
        : WriteResultReferenceFailureContractTests
    {
        private UpdateResult.UpdateFailureReference _result = null!;

        [SetUp]
        public void Setup()
        {
            var targetResource = new BaseResourceInfo(
                ProjectName: new ProjectName("ed-fi"),
                ResourceName: new ResourceName("EducationOrganization"),
                IsDescriptor: false
            );

            _result = new UpdateResult.UpdateFailureReference([
                new(
                    Path: new JsonPath("$.educationOrganizationReference"),
                    TargetResource: targetResource,
                    DocumentIdentity: new([
                        new(
                            new JsonPath("$.educationOrganizationReference.educationOrganizationId"),
                            "255901"
                        ),
                    ]),
                    ReferentialId: new ReferentialId(Guid.NewGuid()),
                    Reason: DocumentReferenceFailureReason.IncompatibleTargetType
                ),
            ]);
        }

        [Test]
        public void It_carries_the_reason_and_target_resource_for_the_failing_path()
        {
            _result.InvalidDocumentReferences.Should().ContainSingle();
            _result.InvalidDocumentReferences[0].Path.Value.Should().Be("$.educationOrganizationReference");
            _result
                .InvalidDocumentReferences[0]
                .TargetResource.ResourceName.Value.Should()
                .Be("EducationOrganization");
            _result
                .InvalidDocumentReferences[0]
                .Reason.Should()
                .Be(DocumentReferenceFailureReason.IncompatibleTargetType);
        }

        [Test]
        public void It_retains_the_existing_deduped_resource_name_projection()
        {
            _result.GetResourceNames().Should().Equal(new ResourceName("EducationOrganization"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_UpsertFailureDescriptorReference_With_A_Descriptor_Type_Mismatch
        : WriteResultReferenceFailureContractTests
    {
        private UpsertResult.UpsertFailureDescriptorReference _result = null!;

        [SetUp]
        public void Setup()
        {
            var targetResource = new BaseResourceInfo(
                ProjectName: new ProjectName("ed-fi"),
                ResourceName: new ResourceName("SchoolTypeDescriptor"),
                IsDescriptor: true
            );

            _result = new UpsertResult.UpsertFailureDescriptorReference([
                new(
                    Path: new JsonPath("$.schoolTypeDescriptor"),
                    TargetResource: targetResource,
                    DocumentIdentity: new([
                        new(
                            DocumentIdentity.DescriptorIdentityJsonPath,
                            "uri://ed-fi.org/gradeleveldescriptor#first-grade"
                        ),
                    ]),
                    ReferentialId: new ReferentialId(Guid.NewGuid()),
                    Reason: DescriptorReferenceFailureReason.DescriptorTypeMismatch
                ),
            ]);
        }

        [Test]
        public void It_carries_the_reason_and_target_resource_for_the_failing_path()
        {
            _result.InvalidDescriptorReferences.Should().ContainSingle();
            _result.InvalidDescriptorReferences[0].Path.Value.Should().Be("$.schoolTypeDescriptor");
            _result
                .InvalidDescriptorReferences[0]
                .TargetResource.ResourceName.Value.Should()
                .Be("SchoolTypeDescriptor");
            _result
                .InvalidDescriptorReferences[0]
                .Reason.Should()
                .Be(DescriptorReferenceFailureReason.DescriptorTypeMismatch);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_UpdateFailureDescriptorReference_With_A_Missing_Descriptor
        : WriteResultReferenceFailureContractTests
    {
        private UpdateResult.UpdateFailureDescriptorReference _result = null!;

        [SetUp]
        public void Setup()
        {
            var targetResource = new BaseResourceInfo(
                ProjectName: new ProjectName("ed-fi"),
                ResourceName: new ResourceName("CalendarTypeDescriptor"),
                IsDescriptor: true
            );

            _result = new UpdateResult.UpdateFailureDescriptorReference([
                new(
                    Path: new JsonPath("$.calendarReference.calendarTypeDescriptor"),
                    TargetResource: targetResource,
                    DocumentIdentity: new([
                        new(
                            DocumentIdentity.DescriptorIdentityJsonPath,
                            "uri://ed-fi.org/calendartypedescriptor#spring"
                        ),
                    ]),
                    ReferentialId: new ReferentialId(Guid.NewGuid()),
                    Reason: DescriptorReferenceFailureReason.Missing
                ),
            ]);
        }

        [Test]
        public void It_preserves_the_missing_reason_for_the_concrete_path()
        {
            _result.InvalidDescriptorReferences.Should().ContainSingle();
            _result
                .InvalidDescriptorReferences[0]
                .Path.Value.Should()
                .Be("$.calendarReference.calendarTypeDescriptor");
            _result
                .InvalidDescriptorReferences[0]
                .Reason.Should()
                .Be(DescriptorReferenceFailureReason.Missing);
        }
    }
}
