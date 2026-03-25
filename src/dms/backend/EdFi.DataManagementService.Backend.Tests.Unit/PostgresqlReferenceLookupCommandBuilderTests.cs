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

    [Test]
    public void It_emits_a_single_uuid_array_parameter_for_empty_lookup_sets()
    {
        var command = PostgresqlReferenceLookupCommandBuilder.Build([]);

        command.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@referentialIds");
        ((Guid[])command.Parameters[0].Value!).Should().BeEmpty();

        var npgsqlParameter = new NpgsqlParameter();
        command.Parameters[0].ConfigureParameter.Should().NotBeNull();
        command.Parameters[0].ConfigureParameter!(npgsqlParameter);
        npgsqlParameter.NpgsqlDbType.Should().Be(ReferentialIdsParameterDbType);

        command.CommandText.Should().Contain("unnest(@referentialIds::uuid[]) WITH ORDINALITY");
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

        var command = PostgresqlReferenceLookupCommandBuilder.Build(referentialIds);

        ((Guid[])command.Parameters[0].Value!)
            .Should()
            .Equal(referentialIds.Select(static referentialId => referentialId.Value));
    }

    [Test]
    public void It_uses_the_same_bulk_sql_shape_for_large_lookup_sets_without_per_reference_parameters()
    {
        ReferentialId[] referentialIds = [.. Enumerable.Range(1, 5000).Select(CreateReferentialId)];

        var emptyCommand = PostgresqlReferenceLookupCommandBuilder.Build([]);
        var largeCommand = PostgresqlReferenceLookupCommandBuilder.Build(referentialIds);

        largeCommand.CommandText.Should().Be(emptyCommand.CommandText);
        largeCommand.Parameters.Should().ContainSingle();

        var parameterValues = (Guid[])largeCommand.Parameters[0].Value!;
        parameterValues.Should().HaveCount(5000);
        parameterValues[0].Should().Be(referentialIds[0].Value);
        parameterValues[^1].Should().Be(referentialIds[^1].Value);
    }

    private static ReferentialId CreateReferentialId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 31).CopyTo(bytes, 4);

        return new ReferentialId(new Guid(bytes));
    }
}
