// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_ReferenceLookupVerificationSupport_When_reusing_projection_metadata
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    private IReadOnlyList<ReferenceLookupVerificationProjection> _schoolOnlyProjections = null!;
    private IReadOnlyList<ReferenceLookupVerificationProjection> _schoolAndEdOrgProjections = null!;

    [SetUp]
    public void Setup()
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(_requestResource);

        _schoolOnlyProjections = ReferenceLookupVerificationSupport.BuildProjections(
            new ReferenceLookupRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                Lookups:
                [
                    RelationalAccessTestData.CreateSchoolLookup(
                        ReferenceLookupVerificationSupportTestData.CreateReferentialId(1)
                    ),
                ]
            )
        );
        _schoolAndEdOrgProjections = ReferenceLookupVerificationSupport.BuildProjections(
            new ReferenceLookupRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                Lookups:
                [
                    RelationalAccessTestData.CreateSchoolLookup(
                        ReferenceLookupVerificationSupportTestData.CreateReferentialId(2)
                    ),
                    RelationalAccessTestData.CreateEducationOrganizationLookup(
                        ReferenceLookupVerificationSupportTestData.CreateReferentialId(3)
                    ),
                ]
            )
        );
    }

    [Test]
    public void It_caches_projection_metadata_by_mapping_set_and_resource_identity_shape()
    {
        _schoolOnlyProjections.Should().ContainSingle();
        _schoolAndEdOrgProjections.Should().HaveCount(2);
        _schoolAndEdOrgProjections[0].Should().BeSameAs(_schoolOnlyProjections[0]);
    }
}

[TestFixture]
public class Given_ReferenceLookupVerificationSupport_When_duplicate_resource_identity_shape_mismatches
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(_requestResource);

        try
        {
            ReferenceLookupVerificationSupport.BuildProjections(
                new ReferenceLookupRequest(
                    MappingSet: mappingSet,
                    RequestResource: _requestResource,
                    Lookups:
                    [
                        RelationalAccessTestData.CreateSchoolLookup(
                            ReferenceLookupVerificationSupportTestData.CreateReferentialId(1)
                        ),
                        CreateSchoolLookupWithIdentityPath(
                            ReferenceLookupVerificationSupportTestData.CreateReferentialId(2),
                            "$.alternateSchoolId"
                        ),
                    ]
                )
            );
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    [Test]
    public void It_rejects_duplicate_resource_lookups_with_different_identity_path_orderings()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Match(
                "*Reference lookup verification metadata lookup failed for target 'Ed-Fi.School': "
                    + "multiple lookup entries for the same resource used different identity path orderings.*"
            );
    }

    private static ReferenceLookupRequestEntry CreateSchoolLookupWithIdentityPath(
        ReferentialId referentialId,
        string identityJsonPath
    )
    {
        var requestedIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath(identityJsonPath), "255901"),
        ]);

        return new ReferenceLookupRequestEntry(
            referentialId,
            _schoolResource,
            requestedIdentity,
            ReferenceLookupVerificationSupport.BuildExpectedVerificationIdentityKey(requestedIdentity)
        );
    }
}

internal static class ReferenceLookupVerificationSupportTestData
{
    public static ReferentialId CreateReferentialId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 31).CopyTo(bytes, 4);

        return new ReferentialId(new Guid(bytes));
    }
}
