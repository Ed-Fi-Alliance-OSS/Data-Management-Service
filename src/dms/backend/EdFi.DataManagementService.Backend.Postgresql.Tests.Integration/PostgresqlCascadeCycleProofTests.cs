// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Reciprocal_Identity_Cycle_With_Fixed_Native_Cascades
{
    private static readonly Guid _cycleADocumentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _cycleBDocumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _cycleAForeignKeys = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _cycleBForeignKeys = null!;
    private bool _key1OnlyMutationWasCorrelated;
    private bool _key2OnlyMutationWasCorrelated;
    private bool _allComponentsMutationWasCorrelated;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await _database.ExecuteNonQueryAsync(
            """
            CREATE TABLE public.cycle_a
            (
                document_id uuid NOT NULL,
                key1 integer NOT NULL,
                key2 integer NOT NULL,
                b_document_id uuid NOT NULL,
                CONSTRAINT pk_cycle_a PRIMARY KEY (document_id),
                CONSTRAINT ux_cycle_a_propagation_key UNIQUE (key1, key2, document_id)
            );

            CREATE TABLE public.cycle_b
            (
                document_id uuid NOT NULL,
                key1 integer NOT NULL,
                key2 integer NOT NULL,
                a_document_id uuid NULL,
                CONSTRAINT pk_cycle_b PRIMARY KEY (document_id),
                CONSTRAINT ux_cycle_b_propagation_key UNIQUE (key1, key2, document_id)
            );

            ALTER TABLE public.cycle_a
                ADD CONSTRAINT fk_cycle_a_cycle_b
                FOREIGN KEY (key1, key2, b_document_id)
                REFERENCES public.cycle_b (key1, key2, document_id)
                ON UPDATE CASCADE;

            ALTER TABLE public.cycle_b
                ADD CONSTRAINT fk_cycle_b_cycle_a
                FOREIGN KEY (key1, key2, a_document_id)
                REFERENCES public.cycle_a (key1, key2, document_id)
                ON UPDATE CASCADE;
            """
        );

        _cycleAForeignKeys = await _database.GetForeignKeyMetadataAsync("public", "cycle_a");
        _cycleBForeignKeys = await _database.GetForeignKeyMetadataAsync("public", "cycle_b");

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO public.cycle_b (document_id, key1, key2, a_document_id)
            VALUES (@cycleBDocumentId, 10, 20, NULL);

            INSERT INTO public.cycle_a (document_id, key1, key2, b_document_id)
            VALUES (@cycleADocumentId, 10, 20, @cycleBDocumentId);

            UPDATE public.cycle_b
            SET a_document_id = @cycleADocumentId
            WHERE document_id = @cycleBDocumentId;
            """,
            new NpgsqlParameter("cycleADocumentId", _cycleADocumentId),
            new NpgsqlParameter("cycleBDocumentId", _cycleBDocumentId)
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE public.cycle_b
            SET key1 = 11
            WHERE document_id = @cycleBDocumentId;
            """,
            new NpgsqlParameter("cycleBDocumentId", _cycleBDocumentId)
        );
        _key1OnlyMutationWasCorrelated = await CycleRowsAreCorrelatedAsync(11, 20);

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE public.cycle_b
            SET key2 = 21
            WHERE document_id = @cycleBDocumentId;
            """,
            new NpgsqlParameter("cycleBDocumentId", _cycleBDocumentId)
        );
        _key2OnlyMutationWasCorrelated = await CycleRowsAreCorrelatedAsync(11, 21);

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE public.cycle_b
            SET key1 = 12, key2 = 22
            WHERE document_id = @cycleBDocumentId;
            """,
            new NpgsqlParameter("cycleBDocumentId", _cycleBDocumentId)
        );
        _allComponentsMutationWasCorrelated = await CycleRowsAreCorrelatedAsync(12, 22);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_installs_the_full_cascade_cycle_without_provider_pruning()
    {
        _cycleAForeignKeys.Single().UpdateAction.Should().Be("CASCADE");
        _cycleBForeignKeys.Single().UpdateAction.Should().Be("CASCADE");
    }

    [Test]
    public void It_propagates_each_supported_primitive_mutation_subset()
    {
        _key1OnlyMutationWasCorrelated.Should().BeTrue();
        _key2OnlyMutationWasCorrelated.Should().BeTrue();
        _allComponentsMutationWasCorrelated.Should().BeTrue();
    }

    private Task<bool> CycleRowsAreCorrelatedAsync(int expectedKey1, int expectedKey2) =>
        _database.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS
            (
                SELECT 1
                FROM public.cycle_a AS a
                INNER JOIN public.cycle_b AS b
                    ON b.document_id = a.b_document_id
                   AND a.document_id = b.a_document_id
                WHERE a.document_id = @cycleADocumentId
                  AND b.document_id = @cycleBDocumentId
                  AND a.key1 = @expectedKey1
                  AND b.key1 = @expectedKey1
                  AND a.key2 = @expectedKey2
                  AND b.key2 = @expectedKey2
            );
            """,
            new NpgsqlParameter("cycleADocumentId", _cycleADocumentId),
            new NpgsqlParameter("cycleBDocumentId", _cycleBDocumentId),
            new NpgsqlParameter("expectedKey1", expectedKey1),
            new NpgsqlParameter("expectedKey2", expectedKey2)
        );
}
