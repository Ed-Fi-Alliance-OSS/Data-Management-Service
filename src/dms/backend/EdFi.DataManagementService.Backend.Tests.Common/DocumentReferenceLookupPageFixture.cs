// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Shared provisioning and seed data for the page-level document-reference lookup integration
/// tests, so PostgreSQL and SQL Server assert against identical documents and references.
/// </summary>
/// <remarks>
/// The referenced document ids are chosen to exercise duplicate collapsing rather than to model
/// realistic Ed-Fi semantics. Document 601 references distinct documents from several columns and
/// from its child rows; 602 repeats 601's section in a different row; 603 leaves every reference
/// null; 605 repeats one id across all three of its reference columns; and 604 sits outside the
/// page and is the sole referent of <see cref="OffPageProgram"/>.
/// </remarks>
public static class DocumentReferenceLookupPageFixture
{
    public const long FullyPopulatedDocumentId = 601;
    public const long PartiallyPopulatedDocumentId = 602;
    public const long AllNullReferencesDocumentId = 603;
    public const long OffPageDocumentId = 604;
    public const long RepeatedReferenceDocumentId = 605;

    public const long StudentA = 701;
    public const long StudentB = 702;
    public const long Section = 710;
    public const long DualCreditEdOrg = 720;
    public const long Program = 730;
    public const long OffPageProgram = 740;

    public const long AttemptStatusDescriptorId = 800;
    public const string AttemptStatusUri = "uri://ed-fi.org/AttemptStatusDescriptor#Active";

    public const short StudentResourceKeyId = 21;
    public const short SectionResourceKeyId = 22;
    public const short EducationOrganizationResourceKeyId = 23;
    public const short ProgramResourceKeyId = 24;

    /// <summary>
    /// Document ids that make up the page under test, in ascending order. Excludes
    /// <see cref="OffPageDocumentId"/>.
    /// </summary>
    public static readonly long[] PageDocumentIdsInOrder =
    [
        FullyPopulatedDocumentId,
        PartiallyPopulatedDocumentId,
        AllNullReferencesDocumentId,
        RepeatedReferenceDocumentId,
    ];

    /// <summary>
    /// Distinct referenced document ids the lookup must return for the page, in ascending order.
    /// </summary>
    public static readonly long[] ExpectedReferencedDocumentIdsInOrder =
    [
        StudentA,
        StudentB,
        Section,
        DualCreditEdOrg,
        Program,
    ];

    /// <summary>
    /// Deterministic <c>DocumentUuid</c> for a seeded document id.
    /// </summary>
    public static Guid UuidFor(long documentId) =>
        Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{documentId:D12}"));

    public static string PostgresqlProvisionSql(string schema) =>
        $"""
            DROP SCHEMA IF EXISTS {schema} CASCADE;
            CREATE SCHEMA {schema};
            CREATE SCHEMA IF NOT EXISTS dms;

            CREATE TABLE IF NOT EXISTS dms."Document" (
                "DocumentId" bigint PRIMARY KEY,
                "DocumentUuid" uuid NOT NULL,
                "ResourceKeyId" smallint NOT NULL DEFAULT 0,
                "ContentVersion" bigint NOT NULL DEFAULT 1,
                "IdentityVersion" bigint NOT NULL DEFAULT 1,
                "ContentLastModifiedAt" timestamptz NOT NULL DEFAULT now(),
                "IdentityLastModifiedAt" timestamptz NOT NULL DEFAULT now(),
                "CreatedAt" timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS dms."Descriptor" (
                "DocumentId" bigint PRIMARY KEY,
                "Namespace" varchar(255) NOT NULL DEFAULT '',
                "CodeValue" varchar(50) NOT NULL DEFAULT '',
                "ShortDescription" varchar(75) NOT NULL DEFAULT '',
                "Description" varchar(1024) NULL,
                "EffectiveBeginDate" date NULL,
                "EffectiveEndDate" date NULL,
                "Discriminator" varchar(128) NOT NULL DEFAULT '',
                "Uri" varchar(306) NOT NULL
            );

            CREATE TABLE {schema}."StudentSectionAssociation" (
                "DocumentId" bigint PRIMARY KEY,
                "DualCreditEducationOrganization_DocumentId" bigint NULL,
                "DualCreditEducationOrganization_EducationOrganizationId" bigint NULL,
                "Section_DocumentId" bigint NULL,
                "Section_SectionIdentifier" varchar(255) NULL,
                "Student_DocumentId" bigint NULL,
                "Student_StudentUniqueId" varchar(32) NULL,
                "AttemptStatusDescriptor_DescriptorId" bigint NULL
            );

            CREATE TABLE {schema}."StudentSectionAssociationProgram" (
                "CollectionItemId" bigint PRIMARY KEY,
                "StudentSectionAssociation_DocumentId" bigint NOT NULL,
                "Ordinal" integer NOT NULL,
                "Program_DocumentId" bigint NULL,
                "Program_ProgramName" varchar(60) NULL
            );
            """;

    public static string PostgresqlSeedSql(string schema) =>
        $"""
            {PostgresqlDeleteSeedRowsSql}

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
            VALUES
                ({FullyPopulatedDocumentId}, '{UuidFor(FullyPopulatedDocumentId)}', 20),
                ({PartiallyPopulatedDocumentId}, '{UuidFor(PartiallyPopulatedDocumentId)}', 20),
                ({AllNullReferencesDocumentId}, '{UuidFor(AllNullReferencesDocumentId)}', 20),
                ({OffPageDocumentId}, '{UuidFor(OffPageDocumentId)}', 20),
                ({RepeatedReferenceDocumentId}, '{UuidFor(RepeatedReferenceDocumentId)}', 20),
                ({StudentA}, '{UuidFor(StudentA)}', {StudentResourceKeyId}),
                ({StudentB}, '{UuidFor(StudentB)}', {StudentResourceKeyId}),
                ({Section}, '{UuidFor(Section)}', {SectionResourceKeyId}),
                ({DualCreditEdOrg}, '{UuidFor(DualCreditEdOrg)}', {EducationOrganizationResourceKeyId}),
                ({Program}, '{UuidFor(Program)}', {ProgramResourceKeyId}),
                ({OffPageProgram}, '{UuidFor(OffPageProgram)}', {ProgramResourceKeyId});

            INSERT INTO dms."Descriptor" ("DocumentId", "Uri")
            VALUES ({AttemptStatusDescriptorId}, '{AttemptStatusUri}');

            INSERT INTO {schema}."StudentSectionAssociation"
            VALUES
                ({FullyPopulatedDocumentId}, {DualCreditEdOrg}, 255901, {Section}, 'SEC-X', {StudentA}, 'S-701', {AttemptStatusDescriptorId}),
                ({PartiallyPopulatedDocumentId}, NULL, NULL, {Section}, 'SEC-X', {StudentB}, 'S-702', NULL),
                ({AllNullReferencesDocumentId}, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                ({OffPageDocumentId}, NULL, NULL, {Section}, 'SEC-X', {StudentA}, 'S-701', NULL),
                ({RepeatedReferenceDocumentId}, {Program}, 255902, {Program}, 'SEC-DUP', {Program}, 'S-730', NULL);

            INSERT INTO {schema}."StudentSectionAssociationProgram"
            VALUES
                (1, {FullyPopulatedDocumentId}, 0, {Program}, 'Program P'),
                (2, {FullyPopulatedDocumentId}, 1, {StudentA}, 'Program Named Like Student'),
                (3, {OffPageDocumentId}, 0, {OffPageProgram}, 'Program Q');
            """;

    public static string PostgresqlCleanupSql(string schema) =>
        $"""
            DROP SCHEMA IF EXISTS {schema} CASCADE;
            {PostgresqlDeleteSeedRowsSql}
            """;

    private static string PostgresqlDeleteSeedRowsSql =>
        $"""
            DELETE FROM dms."Document" WHERE "DocumentId" IN (
                {FullyPopulatedDocumentId}, {PartiallyPopulatedDocumentId}, {AllNullReferencesDocumentId},
                {OffPageDocumentId}, {RepeatedReferenceDocumentId}, {StudentA}, {StudentB}, {Section},
                {DualCreditEdOrg}, {Program}, {OffPageProgram});
            DELETE FROM dms."Descriptor" WHERE "DocumentId" = {AttemptStatusDescriptorId};
            """;

    public static string MssqlProvisionSql(string schema) =>
        $"""
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{schema}') EXEC('CREATE SCHEMA [{schema}]');

            IF OBJECT_ID('dms.Document', 'U') IS NULL
            CREATE TABLE dms.[Document] (
                [DocumentId] bigint PRIMARY KEY,
                [DocumentUuid] uniqueidentifier NOT NULL,
                [ResourceKeyId] smallint NOT NULL DEFAULT 0,
                [ContentVersion] bigint NOT NULL DEFAULT 1,
                [IdentityVersion] bigint NOT NULL DEFAULT 1,
                [ContentLastModifiedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                [IdentityLastModifiedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                [CreatedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            IF OBJECT_ID('dms.Descriptor', 'U') IS NULL
            CREATE TABLE dms.[Descriptor] (
                [DocumentId] bigint PRIMARY KEY,
                [Namespace] varchar(255) NOT NULL DEFAULT '',
                [CodeValue] varchar(50) NOT NULL DEFAULT '',
                [ShortDescription] varchar(75) NOT NULL DEFAULT '',
                [Description] varchar(1024) NULL,
                [EffectiveBeginDate] date NULL,
                [EffectiveEndDate] date NULL,
                [Discriminator] varchar(128) NOT NULL DEFAULT '',
                [Uri] varchar(306) NOT NULL
            );

            CREATE TABLE {schema}.[StudentSectionAssociation] (
                [DocumentId] bigint PRIMARY KEY,
                [DualCreditEducationOrganization_DocumentId] bigint NULL,
                [DualCreditEducationOrganization_EducationOrganizationId] bigint NULL,
                [Section_DocumentId] bigint NULL,
                [Section_SectionIdentifier] varchar(255) NULL,
                [Student_DocumentId] bigint NULL,
                [Student_StudentUniqueId] varchar(32) NULL,
                [AttemptStatusDescriptor_DescriptorId] bigint NULL
            );

            CREATE TABLE {schema}.[StudentSectionAssociationProgram] (
                [CollectionItemId] bigint PRIMARY KEY,
                [StudentSectionAssociation_DocumentId] bigint NOT NULL,
                [Ordinal] int NOT NULL,
                [Program_DocumentId] bigint NULL,
                [Program_ProgramName] varchar(60) NULL
            );
            """;

    public static string MssqlSeedSql(string schema) =>
        $"""
            INSERT INTO dms.[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId])
            VALUES
                ({FullyPopulatedDocumentId}, '{UuidFor(FullyPopulatedDocumentId)}', 20),
                ({PartiallyPopulatedDocumentId}, '{UuidFor(PartiallyPopulatedDocumentId)}', 20),
                ({AllNullReferencesDocumentId}, '{UuidFor(AllNullReferencesDocumentId)}', 20),
                ({OffPageDocumentId}, '{UuidFor(OffPageDocumentId)}', 20),
                ({RepeatedReferenceDocumentId}, '{UuidFor(RepeatedReferenceDocumentId)}', 20),
                ({StudentA}, '{UuidFor(StudentA)}', {StudentResourceKeyId}),
                ({StudentB}, '{UuidFor(StudentB)}', {StudentResourceKeyId}),
                ({Section}, '{UuidFor(Section)}', {SectionResourceKeyId}),
                ({DualCreditEdOrg}, '{UuidFor(DualCreditEdOrg)}', {EducationOrganizationResourceKeyId}),
                ({Program}, '{UuidFor(Program)}', {ProgramResourceKeyId}),
                ({OffPageProgram}, '{UuidFor(OffPageProgram)}', {ProgramResourceKeyId});

            INSERT INTO dms.[Descriptor] ([DocumentId], [Uri])
            VALUES ({AttemptStatusDescriptorId}, '{AttemptStatusUri}');

            INSERT INTO {schema}.[StudentSectionAssociation]
            VALUES
                ({FullyPopulatedDocumentId}, {DualCreditEdOrg}, 255901, {Section}, 'SEC-X', {StudentA}, 'S-701', {AttemptStatusDescriptorId}),
                ({PartiallyPopulatedDocumentId}, NULL, NULL, {Section}, 'SEC-X', {StudentB}, 'S-702', NULL),
                ({AllNullReferencesDocumentId}, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                ({OffPageDocumentId}, NULL, NULL, {Section}, 'SEC-X', {StudentA}, 'S-701', NULL),
                ({RepeatedReferenceDocumentId}, {Program}, 255902, {Program}, 'SEC-DUP', {Program}, 'S-730', NULL);

            INSERT INTO {schema}.[StudentSectionAssociationProgram]
            VALUES
                (1, {FullyPopulatedDocumentId}, 0, {Program}, 'Program P'),
                (2, {FullyPopulatedDocumentId}, 1, {StudentA}, 'Program Named Like Student'),
                (3, {OffPageDocumentId}, 0, {OffPageProgram}, 'Program Q');
            """;
}
