// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
[Parallelizable]
public class ReferenceHelperDescriptorFailureTests
{
    [Test]
    public void It_classifies_missing_descriptors_as_missing_without_parsing_uri_text()
    {
        var missingSameTypeReferentialId = Guid.NewGuid();
        var missingDifferentTypeReferentialId = Guid.NewGuid();
        var malformedUriReferentialId = Guid.NewGuid();

        var failures = ReferenceHelper.DescriptorReferenceFailuresFrom(
            [
                CreateDescriptorReference(
                    missingSameTypeReferentialId,
                    "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                    "$.schoolTypeDescriptor"
                ),
                CreateDescriptorReference(
                    missingDifferentTypeReferentialId,
                    "uri://ed-fi.org/AcademicSubjectDescriptor#English",
                    "$.programs[0].schoolTypeDescriptor"
                ),
                CreateDescriptorReference(
                    malformedUriReferentialId,
                    "not-a-standard-descriptor-uri",
                    "$.programs[1].schoolTypeDescriptor"
                ),
            ],
            [missingSameTypeReferentialId, missingDifferentTypeReferentialId, malformedUriReferentialId]
        );

        failures
            .Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.programs[0].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.programs[1].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing)
            );
    }

    private static DescriptorReference CreateDescriptorReference(
        Guid referentialId,
        string uri,
        string path
    ) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("SchoolTypeDescriptor"),
                IsDescriptor: true
            ),
            DocumentIdentity: new([
                new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri),
            ]),
            ReferentialId: new ReferentialId(referentialId),
            Path: new JsonPath(path)
        );
}
