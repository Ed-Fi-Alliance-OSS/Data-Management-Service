// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlReferenceResolverAdapter
{
    private static readonly EdFi.DataManagementService.Backend.External.QualifiedResourceName _requestResource =
        new("Ed-Fi", "Student");

    [Test]
    public async Task It_matches_postgresql_raw_lookup_results_for_found_missing_incompatible_target_and_descriptor_rows()
    {
        var foundReferentialId = new ReferentialId(Guid.NewGuid());
        var missingReferentialId = new ReferentialId(Guid.NewGuid());
        var incompatibleTargetReferentialId = new ReferentialId(Guid.NewGuid());
        var descriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var request = CreateRequest([
            foundReferentialId,
            missingReferentialId,
            incompatibleTargetReferentialId,
            descriptorReferentialId,
        ]);
        var mssqlExecutor = new InMemoryRelationalCommandExecutor([
            CreateLookupExecution(
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", foundReferentialId.Value),
                    ("DocumentId", 101L),
                    ("ResourceKeyId", (short)11),
                    ("ReferentialIdentityResourceKeyId", (short)11),
                    ("IsDescriptor", false)
                ),
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", incompatibleTargetReferentialId.Value),
                    ("DocumentId", 202L),
                    ("ResourceKeyId", (short)90),
                    ("ReferentialIdentityResourceKeyId", (short)90),
                    ("IsDescriptor", false)
                ),
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", descriptorReferentialId.Value),
                    ("DocumentId", 303L),
                    ("ResourceKeyId", (short)40),
                    ("ReferentialIdentityResourceKeyId", (short)40),
                    ("IsDescriptor", true)
                )
            ),
        ]);
        var postgresqlExecutor = new InMemoryRelationalCommandExecutor([
            CreateLookupExecution(
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", foundReferentialId.Value),
                    ("DocumentId", 101L),
                    ("ResourceKeyId", (short)11),
                    ("ReferentialIdentityResourceKeyId", (short)11),
                    ("IsDescriptor", false)
                ),
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", incompatibleTargetReferentialId.Value),
                    ("DocumentId", 202L),
                    ("ResourceKeyId", (short)90),
                    ("ReferentialIdentityResourceKeyId", (short)90),
                    ("IsDescriptor", false)
                ),
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", descriptorReferentialId.Value),
                    ("DocumentId", 303L),
                    ("ResourceKeyId", (short)40),
                    ("ReferentialIdentityResourceKeyId", (short)40),
                    ("IsDescriptor", true)
                )
            ),
        ]);
        var sut = new MssqlReferenceResolverAdapter(mssqlExecutor);
        var postgresqlAdapter = new PostgresqlReferenceResolverAdapter(postgresqlExecutor);

        var result = await sut.ResolveAsync(request);
        var postgresqlResult = await postgresqlAdapter.ResolveAsync(request);

        mssqlExecutor.Commands.Should().ContainSingle();
        mssqlExecutor.Commands[0].CommandText.Should().Contain("FROM (VALUES");
        postgresqlExecutor.Commands.Should().ContainSingle();
        postgresqlExecutor
            .Commands[0]
            .CommandText.Should()
            .Contain("unnest(@referentialIds::uuid[]) WITH ORDINALITY");
        result.Should().Equal(postgresqlResult);
        result
            .Should()
            .Equal(
                new ReferenceLookupResult(foundReferentialId, 101L, 11, 11, false),
                new ReferenceLookupResult(incompatibleTargetReferentialId, 202L, 90, 90, false),
                new ReferenceLookupResult(descriptorReferentialId, 303L, 40, 40, true)
            );
    }

    [Test]
    public async Task It_switches_to_the_bulk_strategy_at_the_threshold_boundary()
    {
        var firstFoundReferentialId = CreateReferentialId(1);
        var missingReferentialId = CreateReferentialId(2);
        var descriptorReferentialId = CreateReferentialId(3);
        ReferentialId[] referentialIds =
        [
            firstFoundReferentialId,
            missingReferentialId,
            descriptorReferentialId,
            .. Enumerable
                .Range(4, MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold - 3)
                .Select(CreateReferentialId),
        ];
        var executor = new InMemoryRelationalCommandExecutor([
            CreateLookupExecution(
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", descriptorReferentialId.Value),
                    ("DocumentId", 303L),
                    ("ResourceKeyId", (short)40),
                    ("ReferentialIdentityResourceKeyId", (short)40),
                    ("IsDescriptor", true)
                ),
                RelationalAccessTestData.CreateRow(
                    ("ReferentialId", firstFoundReferentialId.Value),
                    ("DocumentId", 101L),
                    ("ResourceKeyId", (short)11),
                    ("ReferentialIdentityResourceKeyId", (short)11),
                    ("IsDescriptor", false)
                )
            ),
        ]);
        var sut = new MssqlReferenceResolverAdapter(executor);

        var result = await sut.ResolveAsync(CreateRequest(referentialIds));

        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("FROM @referentialIds lookupInput");
        executor.Commands[0].Parameters.Should().ContainSingle();
        executor.Commands[0].Parameters[0].Value.Should().BeOfType<DataTable>();
        ((DataTable)executor.Commands[0].Parameters[0].Value!).Rows.Should().HaveCount(referentialIds.Length);
        result
            .Should()
            .Equal(
                new ReferenceLookupResult(firstFoundReferentialId, 101L, 11, 11, false),
                new ReferenceLookupResult(descriptorReferentialId, 303L, 40, 40, true)
            );
    }

    private static ReferenceLookupRequest CreateRequest(IReadOnlyList<ReferentialId> referentialIds) =>
        new(
            MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
            RequestResource: _requestResource,
            ReferentialIds: referentialIds
        );

    private static InMemoryRelationalCommandExecution CreateLookupExecution(
        params IReadOnlyDictionary<string, object?>[] rows
    ) => new([InMemoryRelationalResultSet.Create(rows)]);

    private static ReferentialId CreateReferentialId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 31).CopyTo(bytes, 4);

        return new ReferentialId(new Guid(bytes));
    }
}
