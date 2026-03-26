// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlReferenceLookupSmallListStrategy
{
    private static readonly EdFi.DataManagementService.Backend.External.QualifiedResourceName _requestResource =
        new("Ed-Fi", "Student");

    [Test]
    public void It_emits_an_empty_zero_row_lookup_shape_without_parameters()
    {
        var command = MssqlReferenceLookupSmallListStrategy.BuildCommand(
            new ReferenceLookupRequest(
                MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                RequestResource: _requestResource,
                Lookups: []
            )
        );

        command.Parameters.Should().BeEmpty();
        command
            .CommandText.Should()
            .Contain(
                "SELECT CAST(NULL AS uniqueidentifier) AS [ReferentialId], CAST(NULL AS int) AS [Ordinal]"
            );
        command.CommandText.Should().Contain("[VerificationIdentity]");
        command.CommandText.Should().Contain("[VerificationIdentityKey]");
        command.CommandText.Should().Contain("INNER JOIN [dms].[ReferentialIdentity]");
        command.CommandText.Should().Contain("LEFT JOIN [dms].[Descriptor]");
        command.CommandText.Should().Contain("ORDER BY lookupInput.[Ordinal]");
    }

    [Test]
    public async Task It_projects_found_missing_alias_and_descriptor_membership_rows_for_supported_small_lookup_sets()
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
        var sut = new MssqlReferenceLookupSmallListStrategy(executor);

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
        executor.Commands[0].CommandText.Should().Contain("FROM (VALUES");
        executor.Commands[0].CommandText.Should().Contain("(@p0, 0)");
        executor.Commands[0].CommandText.Should().Contain("(@p1, 1)");
        executor.Commands[0].CommandText.Should().Contain("(@p2, 2)");
        executor.Commands[0].CommandText.Should().Contain("(@p3, 3)");
        executor.Commands[0].CommandText.Should().Contain("FROM [edfi].[School] source");
        executor.Commands[0].CommandText.Should().Contain("FROM [edfi].[EducationOrganization_View] source");
        executor.Commands[0].Parameters.Should().HaveCount(4);
        executor.Commands[0].Parameters[0].Name.Should().Be("@p0");
        executor.Commands[0].Parameters[0].Value.Should().Be(foundReferentialId.Value);
        executor.Commands[0].Parameters[1].Name.Should().Be("@p1");
        executor.Commands[0].Parameters[1].Value.Should().Be(missingReferentialId.Value);
        executor.Commands[0].Parameters[2].Name.Should().Be("@p2");
        executor.Commands[0].Parameters[2].Value.Should().Be(aliasReferentialId.Value);
        executor.Commands[0].Parameters[3].Name.Should().Be("@p3");
        executor.Commands[0].Parameters[3].Value.Should().Be(descriptorReferentialId.Value);

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

    [Test]
    public void It_preserves_first_seen_parameter_order_for_supported_upper_bound_small_lookup_sets()
    {
        var maximumSupportedSmallLookupCount = MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold - 1;
        ReferentialId[] referentialIds =
        [
            .. Enumerable.Range(1, maximumSupportedSmallLookupCount).Select(CreateReferentialId),
        ];

        var command = MssqlReferenceLookupSmallListStrategy.BuildCommand(
            new ReferenceLookupRequest(
                MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                RequestResource: _requestResource,
                Lookups: [.. referentialIds.Select(RelationalAccessTestData.CreateSchoolLookup)]
            )
        );

        command.Parameters.Should().HaveCount(maximumSupportedSmallLookupCount);
        command.Parameters[0].Name.Should().Be("@p0");
        command.Parameters[0].Value.Should().Be(referentialIds[0].Value);
        command.Parameters[^1].Name.Should().Be($"@p{maximumSupportedSmallLookupCount - 1}");
        command.Parameters[^1].Value.Should().Be(referentialIds[^1].Value);
        command.CommandText.Should().Contain("(@p0, 0)");
        command
            .CommandText.Should()
            .Contain($"(@p{maximumSupportedSmallLookupCount - 1}, {maximumSupportedSmallLookupCount - 1})");

        var sqlParameter = new SqlParameter();
        command.Parameters[0].ConfigureParameter.Should().NotBeNull();
        command.Parameters[0].ConfigureParameter!(sqlParameter);
        sqlParameter.DbType.Should().Be(System.Data.DbType.Guid);
        sqlParameter.SqlDbType.Should().Be(System.Data.SqlDbType.UniqueIdentifier);
    }

    [Test]
    public void It_rejects_lookup_sets_that_require_the_bulk_strategy()
    {
        ReferentialId[] referentialIds =
        [
            .. Enumerable
                .Range(1, MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold)
                .Select(CreateReferentialId),
        ];

        MssqlReferenceLookupSmallListStrategy
            .CanResolve(MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold - 1)
            .Should()
            .BeTrue();
        MssqlReferenceLookupSmallListStrategy
            .CanResolve(MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold)
            .Should()
            .BeFalse();

        Action act = () =>
            MssqlReferenceLookupSmallListStrategy.BuildCommand(
                new ReferenceLookupRequest(
                    MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                    RequestResource: _requestResource,
                    Lookups: [.. referentialIds.Select(RelationalAccessTestData.CreateSchoolLookup)]
                )
            );

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*fewer than 2000 referential ids*");
    }

    private static ReferentialId CreateReferentialId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 31).CopyTo(bytes, 4);

        return new ReferentialId(new Guid(bytes));
    }
}
