// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlReferenceLookupBulkStrategy
{
    private static readonly EdFi.DataManagementService.Backend.External.QualifiedResourceName _requestResource =
        new("Ed-Fi", "Student");

    [Test]
    public void It_emits_a_single_uniqueidentifier_tvp_parameter_for_threshold_crossing_lookup_sets()
    {
        ReferentialId[] referentialIds =
        [
            .. Enumerable
                .Range(1, MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold)
                .Select(CreateReferentialId),
        ];

        MssqlReferenceLookupBulkStrategy
            .CanResolve(MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold - 1)
            .Should()
            .BeFalse();
        MssqlReferenceLookupBulkStrategy
            .CanResolve(MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold)
            .Should()
            .BeTrue();

        var command = MssqlReferenceLookupBulkStrategy.BuildCommand(referentialIds);

        command.CommandText.Should().Contain("FROM @referentialIds lookupInput");
        command.CommandText.Should().Contain("INNER JOIN [dms].[ReferentialIdentity]");
        command.CommandText.Should().Contain("LEFT JOIN [dms].[Descriptor]");
        command.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@referentialIds");
        command.Parameters[0].Value.Should().BeOfType<DataTable>();

        var referentialIdTable = (DataTable)command.Parameters[0].Value!;
        referentialIdTable.Columns.Should().ContainSingle();
        referentialIdTable.Columns[0].ColumnName.Should().Be("Id");
        referentialIdTable.Columns[0].DataType.Should().Be(typeof(Guid));
        referentialIdTable.Rows.Should().HaveCount(referentialIds.Length);
        referentialIdTable.Rows[0].ItemArray.Should().Equal(referentialIds[0].Value);
        referentialIdTable.Rows[^1].ItemArray.Should().Equal(referentialIds[^1].Value);

        var sqlParameter = new SqlParameter();
        command.Parameters[0].ConfigureParameter.Should().NotBeNull();
        command.Parameters[0].ConfigureParameter!(sqlParameter);
        sqlParameter.SqlDbType.Should().Be(SqlDbType.Structured);
        sqlParameter.TypeName.Should().Be("dms.UniqueIdentifierTable");
    }

    [Test]
    public async Task It_returns_bulk_lookup_rows_in_request_order_even_when_sql_server_returns_them_out_of_order()
    {
        var firstFoundReferentialId = CreateReferentialId(1);
        var missingReferentialId = CreateReferentialId(2);
        var aliasReferentialId = CreateReferentialId(3);
        var descriptorReferentialId = CreateReferentialId(4);
        ReferentialId[] referentialIds =
        [
            firstFoundReferentialId,
            missingReferentialId,
            aliasReferentialId,
            descriptorReferentialId,
            .. Enumerable
                .Range(5, MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold - 4)
                .Select(CreateReferentialId),
        ];

        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", descriptorReferentialId.Value),
                        ("DocumentId", 303L),
                        ("ResourceKeyId", (short)40),
                        ("ReferentialIdentityResourceKeyId", (short)40),
                        ("IsDescriptor", true)
                    ),
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", aliasReferentialId.Value),
                        ("DocumentId", 202L),
                        ("ResourceKeyId", (short)21),
                        ("ReferentialIdentityResourceKeyId", (short)30),
                        ("IsDescriptor", false)
                    ),
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", firstFoundReferentialId.Value),
                        ("DocumentId", 101L),
                        ("ResourceKeyId", (short)11),
                        ("ReferentialIdentityResourceKeyId", (short)11),
                        ("IsDescriptor", false)
                    )
                ),
            ]),
        ]);
        var sut = new MssqlReferenceLookupBulkStrategy(executor);

        var result = await sut.ResolveAsync(
            new ReferenceLookupRequest(
                MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                RequestResource: _requestResource,
                ReferentialIds: referentialIds
            )
        );

        executor.Commands.Should().ContainSingle();
        executor.Commands[0].Parameters.Should().ContainSingle();
        executor.Commands[0].Parameters[0].Value.Should().BeOfType<DataTable>();
        ((DataTable)executor.Commands[0].Parameters[0].Value!).Rows.Should().HaveCount(referentialIds.Length);

        result.Should().HaveCount(3);
        result
            .Should()
            .Equal(
                new ReferenceLookupResult(firstFoundReferentialId, 101L, 11, 11, false),
                new ReferenceLookupResult(aliasReferentialId, 202L, 21, 30, false),
                new ReferenceLookupResult(descriptorReferentialId, 303L, 40, 40, true)
            );
    }

    private static ReferentialId CreateReferentialId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 31).CopyTo(bytes, 4);

        return new ReferentialId(new Guid(bytes));
    }
}
