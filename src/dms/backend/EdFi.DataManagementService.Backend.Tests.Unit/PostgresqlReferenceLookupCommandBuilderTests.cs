// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PostgresqlReferenceLookupCommandBuilder
{
    private static readonly NpgsqlDbType ReferentialIdsParameterDbType = (NpgsqlDbType)(
        (int)NpgsqlDbType.Array | (int)NpgsqlDbType.Uuid
    );
    private static readonly EdFi.DataManagementService.Backend.External.QualifiedResourceName _requestResource =
        new("Ed-Fi", "Student");

    [Test]
    public void It_emits_a_single_uuid_array_parameter_for_empty_lookup_sets()
    {
        var command = PostgresqlReferenceLookupCommandBuilder.Build(CreateRequest([]));

        command.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@referentialIds");
        ((Guid[])command.Parameters[0].Value!).Should().BeEmpty();

        var npgsqlParameter = new NpgsqlParameter();
        command.Parameters[0].ConfigureParameter.Should().NotBeNull();
        command.Parameters[0].ConfigureParameter!(npgsqlParameter);
        npgsqlParameter.NpgsqlDbType.Should().Be(ReferentialIdsParameterDbType);

        command.CommandText.Should().Contain("unnest(@referentialIds::uuid[]) WITH ORDINALITY");
        command.CommandText.Should().Contain("\"VerificationIdentity\"");
        command.CommandText.Should().Contain("\"VerificationIdentityKey\"");
        command.CommandText.Should().Contain("INNER JOIN dms.\"ReferentialIdentity\"");
        command.CommandText.Should().Contain("LEFT JOIN dms.\"Descriptor\"");
        command.CommandText.Should().Contain("ORDER BY lookupInput.\"Ordinal\"");
    }

    [Test]
    public void It_preserves_first_seen_referential_id_order_for_small_lookup_sets()
    {
        ReferentialId[] referentialIds =
        [
            CreateReferentialId(11),
            CreateReferentialId(7),
            CreateReferentialId(23),
        ];

        var command = PostgresqlReferenceLookupCommandBuilder.Build(CreateRequest(referentialIds));

        ((Guid[])command.Parameters[0].Value!)
            .Should()
            .Equal(referentialIds.Select(static referentialId => referentialId.Value));
    }

    [Test]
    public void It_uses_the_same_bulk_sql_shape_for_large_lookup_sets_without_per_reference_parameters()
    {
        ReferentialId[] referentialIds = [.. Enumerable.Range(1, 5000).Select(CreateReferentialId)];
        ReferentialId[] shapeReferentialIds =
        [
            CreateReferentialId(7001),
            CreateReferentialId(7002),
            CreateReferentialId(7003),
        ];

        var shapeCommand = PostgresqlReferenceLookupCommandBuilder.Build(CreateRequest(shapeReferentialIds));
        var largeCommand = PostgresqlReferenceLookupCommandBuilder.Build(CreateRequest(referentialIds));

        largeCommand.CommandText.Should().Be(shapeCommand.CommandText);
        largeCommand.Parameters.Should().ContainSingle();

        var parameterValues = (Guid[])largeCommand.Parameters[0].Value!;
        parameterValues.Should().HaveCount(5000);
        parameterValues[0].Should().Be(referentialIds[0].Value);
        parameterValues[^1].Should().Be(referentialIds[^1].Value);
    }

    [Test]
    public void It_projects_authoritative_identity_witnesses_from_concrete_and_abstract_sources()
    {
        var command = PostgresqlReferenceLookupCommandBuilder.Build(
            new ReferenceLookupRequest(
                MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                RequestResource: _requestResource,
                Lookups:
                [
                    RelationalAccessTestData.CreateSchoolLookup(CreateReferentialId(1)),
                    RelationalAccessTestData.CreateEducationOrganizationLookup(CreateReferentialId(2)),
                    RelationalAccessTestData.CreateSchoolTypeDescriptorLookup(CreateReferentialId(3)),
                ]
            )
        );

        command.CommandText.Should().Contain("FROM \"edfi\".\"School\" source");
        command.CommandText.Should().Contain("FROM \"edfi\".\"EducationOrganization_View\" source");
        command.CommandText.Should().Contain("'$.schoolId='");
        command.CommandText.Should().Contain("'$.educationOrganizationId='");
        command.CommandText.Should().Contain("'$.descriptor=' || lower(descriptor.\"Uri\")");
    }

    [Test]
    public void It_formats_datetime_identity_verification_keys_in_core_canonical_utc_shape()
    {
        var command = PostgresqlReferenceLookupCommandBuilder.Build(
            new ReferenceLookupRequest(
                MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                RequestResource: _requestResource,
                Lookups: [RelationalAccessTestData.CreateMeetingLookup(CreateReferentialId(4))]
            )
        );

        command.CommandText.Should().Contain("FROM \"edfi\".\"Meeting\" source");
        command
            .CommandText.Should()
            .Contain(
                @"to_char(source.""MeetingDateTime"" AT TIME ZONE 'UTC', 'YYYY-MM-DD""T""HH24:MI:SS""Z""')"
            );
    }

    private static ReferentialId CreateReferentialId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 31).CopyTo(bytes, 4);

        return new ReferentialId(new Guid(bytes));
    }

    private static ReferenceLookupRequest CreateRequest(IReadOnlyList<ReferentialId> referentialIds) =>
        new(
            MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
            RequestResource: _requestResource,
            Lookups:
            [
                .. referentialIds.Select(
                    (referentialId, index) =>
                        index switch
                        {
                            0 => RelationalAccessTestData.CreateSchoolLookup(referentialId),
                            1 => RelationalAccessTestData.CreateEducationOrganizationLookup(referentialId),
                            _ => RelationalAccessTestData.CreateSchoolTypeDescriptorLookup(referentialId),
                        }
                ),
            ]
        );
}
