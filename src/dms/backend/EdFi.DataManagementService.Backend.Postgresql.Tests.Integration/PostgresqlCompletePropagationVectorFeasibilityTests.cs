// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_The_Widest_Complete_Propagation_Vector_On_Postgresql
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private int _encodedPayloadBytes;
    private long _matchingChildRows;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await _database.ExecuteNonQueryAsync(
            """
            CREATE TABLE public.wide_vector_target
            (
                namespace_value varchar(255) NOT NULL,
                survey_identifier varchar(60) NOT NULL,
                response_identifier varchar(60) NOT NULL,
                section_title varchar(255) NOT NULL,
                response_document_id bigint NOT NULL,
                section_document_id bigint NOT NULL,
                document_id bigint NOT NULL,
                CONSTRAINT ux_wide_vector_target_propagation_key UNIQUE
                (
                    namespace_value,
                    survey_identifier,
                    response_identifier,
                    section_title,
                    response_document_id,
                    section_document_id,
                    document_id
                )
            );

            CREATE TABLE public.wide_vector_child
            (
                child_id bigint NOT NULL PRIMARY KEY,
                namespace_value varchar(255) NOT NULL,
                survey_identifier varchar(60) NOT NULL,
                response_identifier varchar(60) NOT NULL,
                section_title varchar(255) NOT NULL,
                response_document_id bigint NOT NULL,
                section_document_id bigint NOT NULL,
                target_document_id bigint NOT NULL,
                CONSTRAINT fk_wide_vector_child_target FOREIGN KEY
                (
                    namespace_value,
                    survey_identifier,
                    response_identifier,
                    section_title,
                    response_document_id,
                    section_document_id,
                    target_document_id
                )
                REFERENCES public.wide_vector_target
                (
                    namespace_value,
                    survey_identifier,
                    response_identifier,
                    section_title,
                    response_document_id,
                    section_document_id,
                    document_id
                )
            );

            INSERT INTO public.wide_vector_target
            VALUES
            (
                repeat(chr(128512), 255),
                repeat(chr(128513), 60),
                repeat(chr(128514), 60),
                repeat(chr(128515), 255),
                1,
                2,
                3
            );

            INSERT INTO public.wide_vector_child
            VALUES
            (
                1,
                repeat(chr(128512), 255),
                repeat(chr(128513), 60),
                repeat(chr(128514), 60),
                repeat(chr(128515), 255),
                1,
                2,
                3
            );
            """
        );

        _encodedPayloadBytes = await _database.ExecuteScalarAsync<int>(
            """
            SELECT
                octet_length(namespace_value)
                + octet_length(survey_identifier)
                + octet_length(response_identifier)
                + octet_length(section_title)
                + 24
            FROM public.wide_vector_target;
            """
        );
        _matchingChildRows = await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM public.wide_vector_child AS child
            INNER JOIN public.wide_vector_target AS target
                ON target.namespace_value = child.namespace_value
               AND target.survey_identifier = child.survey_identifier
               AND target.response_identifier = child.response_identifier
               AND target.section_title = child.section_title
               AND target.response_document_id = child.response_document_id
               AND target.section_document_id = child.section_document_id
               AND target.document_id = child.target_document_id;
            """
        );
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
    public void It_accepts_the_maximum_declared_full_vector_payload()
    {
        _encodedPayloadBytes.Should().Be(2544);
        _matchingChildRows.Should().Be(1);
    }
}
