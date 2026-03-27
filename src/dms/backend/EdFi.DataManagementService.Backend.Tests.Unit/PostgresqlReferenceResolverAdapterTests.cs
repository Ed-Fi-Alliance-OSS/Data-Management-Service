// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PostgresqlReferenceResolverAdapter
{
    private static readonly EdFi.DataManagementService.Backend.External.QualifiedResourceName _requestResource =
        new("Ed-Fi", "Student");

    [Test]
    public async Task It_projects_found_missing_alias_and_descriptor_membership_rows()
    {
        var foundReferentialId = new ReferentialId(Guid.NewGuid());
        var missingReferentialId = new ReferentialId(Guid.NewGuid());
        var aliasReferentialId = new ReferentialId(Guid.NewGuid());
        var descriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", foundReferentialId.Value),
                        ("DocumentId", 101L),
                        ("ResourceKeyId", (short)11),
                        ("ReferentialIdentityResourceKeyId", (short)11),
                        ("IsDescriptor", false),
                        ("VerificationIdentityKey", "$$.schoolId=255901")
                    ),
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", aliasReferentialId.Value),
                        ("DocumentId", 202L),
                        ("ResourceKeyId", (short)21),
                        ("ReferentialIdentityResourceKeyId", (short)30),
                        ("IsDescriptor", false),
                        ("VerificationIdentityKey", "$$.educationOrganizationId=255901")
                    ),
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", descriptorReferentialId.Value),
                        ("DocumentId", 303L),
                        ("ResourceKeyId", (short)40),
                        ("ReferentialIdentityResourceKeyId", (short)40),
                        ("IsDescriptor", true),
                        (
                            "VerificationIdentityKey",
                            "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                        )
                    )
                ),
            ]),
        ]);
        var sut = new PostgresqlReferenceResolverAdapter(executor);

        var result = await sut.ResolveAsync(
            new ReferenceLookupRequest(
                MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                RequestResource: _requestResource,
                Lookups:
                [
                    RelationalAccessTestData.CreateSchoolLookup(foundReferentialId),
                    RelationalAccessTestData.CreateSchoolLookup(missingReferentialId),
                    RelationalAccessTestData.CreateEducationOrganizationLookup(aliasReferentialId),
                    RelationalAccessTestData.CreateSchoolTypeDescriptorLookup(descriptorReferentialId),
                ]
            )
        );

        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("unnest(@referentialIds::uuid[]) WITH ORDINALITY");
        executor.Commands[0].Parameters.Should().ContainSingle();
        executor.Commands[0].Parameters[0].Name.Should().Be("@referentialIds");
        ((Guid[])executor.Commands[0].Parameters[0].Value!)
            .Should()
            .Equal(
                foundReferentialId.Value,
                missingReferentialId.Value,
                aliasReferentialId.Value,
                descriptorReferentialId.Value
            );

        result.Should().HaveCount(3);
        result
            .Should()
            .Equal(
                new ReferenceLookupResult(foundReferentialId, 101L, 11, 11, false, "$$.schoolId=255901"),
                new ReferenceLookupResult(
                    aliasReferentialId,
                    202L,
                    21,
                    30,
                    false,
                    "$$.educationOrganizationId=255901"
                ),
                new ReferenceLookupResult(
                    descriptorReferentialId,
                    303L,
                    40,
                    40,
                    true,
                    "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                )
            );
    }
}
