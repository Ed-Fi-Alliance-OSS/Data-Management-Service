IF OBJECT_ID('tempdb..[#page]') IS NOT NULL
    DROP TABLE [#page];
CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL);

WITH page_ids AS (
SELECT TOP (@pageSize) r.[DocumentId]
FROM [edfi].[Student] r
WHERE
    ((r.[DocumentId] IN (SELECT t0.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t0 WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0) AND t0.[Student_DocumentId] >= @cursorMin AND t0.[Student_DocumentId] <= @cursorMax)))
    AND (r.[DocumentId] >= @cursorMin)
    AND (r.[DocumentId] <= @cursorMax)
ORDER BY r.[DocumentId] ASC
)
INSERT INTO [#page] ([DocumentId])
OUTPUT INSERTED.[DocumentId]
SELECT [DocumentId] FROM page_ids;

SELECT
    d.[DocumentId],
    d.[DocumentUuid],
    d.[ContentVersion],
    d.[ContentLastModifiedAt],
    d.[ResourceKeyId]
FROM [dms].[Document] d
INNER JOIN [#page] k ON d.[DocumentId] = k.[DocumentId]
ORDER BY COALESCE(k.[Ordinal], d.[DocumentId]), d.[DocumentId];

SELECT
    r.[DocumentId],
    r.[Person_DocumentId],
    r.[Person_PersonId],
    r.[Person_SourceSystemDescriptor_DescriptorId],
    r.[BirthCountryDescriptor_DescriptorId],
    r.[BirthSexDescriptor_DescriptorId],
    r.[BirthStateAbbreviationDescriptor_DescriptorId],
    r.[CitizenshipStatusDescriptor_DescriptorId],
    r.[BirthCity],
    r.[BirthDate],
    r.[BirthInternationalProvince],
    r.[DateEnteredUS],
    r.[FirstName],
    r.[GenerationCodeSuffix],
    r.[LastSurname],
    r.[MaidenName],
    r.[MiddleName],
    r.[MultipleBirthStatus],
    r.[PersonalTitlePrefix],
    r.[PreferredFirstName],
    r.[PreferredLastSurname],
    r.[StudentUniqueId]
FROM [edfi].[Student] r
INNER JOIN [#page] k ON r.[DocumentId] = k.[DocumentId]
ORDER BY
    r.[DocumentId] ASC
;


SELECT
    t.[CollectionItemId],
    t.[Ordinal],
    t.[Student_DocumentId],
    t.[IdentificationDocumentUseDescriptor_DescriptorId],
    t.[IssuerCountryDescriptor_DescriptorId],
    t.[PersonalInformationVerificationDescriptor_DescriptorId],
    t.[DocumentExpirationDate],
    t.[DocumentTitle],
    t.[IssuerDocumentIdentificationCode],
    t.[IssuerName]
FROM [edfi].[StudentIdentificationDocument] t
INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
ORDER BY
    t.[Student_DocumentId] ASC,
    t.[Ordinal] ASC
;


SELECT
    t.[CollectionItemId],
    t.[Ordinal],
    t.[Student_DocumentId],
    t.[OtherNameTypeDescriptor_DescriptorId],
    t.[FirstName],
    t.[GenerationCodeSuffix],
    t.[LastSurname],
    t.[MiddleName],
    t.[PersonalTitlePrefix]
FROM [edfi].[StudentOtherName] t
INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
ORDER BY
    t.[Student_DocumentId] ASC,
    t.[Ordinal] ASC
;


SELECT
    t.[CollectionItemId],
    t.[Ordinal],
    t.[Student_DocumentId],
    t.[IdentificationDocumentUseDescriptor_DescriptorId],
    t.[IssuerCountryDescriptor_DescriptorId],
    t.[PersonalInformationVerificationDescriptor_DescriptorId],
    t.[PersonalDocumentExpirationDate],
    t.[PersonalDocumentTitle],
    t.[PersonalIssuerDocumentIdentificationCode],
    t.[PersonalIssuerName]
FROM [edfi].[StudentPersonalIdentificationDocument] t
INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
ORDER BY
    t.[Student_DocumentId] ASC,
    t.[Ordinal] ASC
;


SELECT
    t.[CollectionItemId],
    t.[Ordinal],
    t.[Student_DocumentId],
    t.[VisaDescriptor_DescriptorId]
FROM [edfi].[StudentVisa] t
INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
ORDER BY
    t.[Student_DocumentId] ASC,
    t.[Ordinal] ASC
;


SELECT
    p.[DescriptorId],
    d.[Uri]
FROM
    (
        SELECT v0.[DescriptorId]
        FROM [edfi].[Student] t0
        INNER JOIN [#page] k ON t0.[DocumentId] = k.[DocumentId]
        CROSS APPLY (VALUES (t0.[Person_SourceSystemDescriptor_DescriptorId]), (t0.[BirthCountryDescriptor_DescriptorId]), (t0.[BirthSexDescriptor_DescriptorId]), (t0.[BirthStateAbbreviationDescriptor_DescriptorId]), (t0.[CitizenshipStatusDescriptor_DescriptorId])) AS v0([DescriptorId])
        WHERE v0.[DescriptorId] IS NOT NULL
        UNION
        SELECT v1.[DescriptorId]
        FROM [edfi].[StudentIdentificationDocument] t1
        INNER JOIN [#page] k ON t1.[Student_DocumentId] = k.[DocumentId]
        CROSS APPLY (VALUES (t1.[IdentificationDocumentUseDescriptor_DescriptorId]), (t1.[IssuerCountryDescriptor_DescriptorId]), (t1.[PersonalInformationVerificationDescriptor_DescriptorId])) AS v1([DescriptorId])
        WHERE v1.[DescriptorId] IS NOT NULL
        UNION
        SELECT t2.[OtherNameTypeDescriptor_DescriptorId] AS [DescriptorId]
        FROM [edfi].[StudentOtherName] t2
        INNER JOIN [#page] k ON t2.[Student_DocumentId] = k.[DocumentId]
        WHERE t2.[OtherNameTypeDescriptor_DescriptorId] IS NOT NULL
        UNION
        SELECT v3.[DescriptorId]
        FROM [edfi].[StudentPersonalIdentificationDocument] t3
        INNER JOIN [#page] k ON t3.[Student_DocumentId] = k.[DocumentId]
        CROSS APPLY (VALUES (t3.[IdentificationDocumentUseDescriptor_DescriptorId]), (t3.[IssuerCountryDescriptor_DescriptorId]), (t3.[PersonalInformationVerificationDescriptor_DescriptorId])) AS v3([DescriptorId])
        WHERE v3.[DescriptorId] IS NOT NULL
        UNION
        SELECT t4.[VisaDescriptor_DescriptorId] AS [DescriptorId]
        FROM [edfi].[StudentVisa] t4
        INNER JOIN [#page] k ON t4.[Student_DocumentId] = k.[DocumentId]
        WHERE t4.[VisaDescriptor_DescriptorId] IS NOT NULL
    ) p
INNER JOIN [dms].[Descriptor] d ON d.[DocumentId] = p.[DescriptorId]
ORDER BY
    p.[DescriptorId] ASC
;


SELECT
    doc.[DocumentId],
    doc.[DocumentUuid],
    doc.[ResourceKeyId]
FROM
    (
        SELECT DISTINCT t0.[Person_DocumentId] AS [DocumentId]
        FROM [edfi].[Student] t0
        INNER JOIN [#page] k ON t0.[DocumentId] = k.[DocumentId]
        WHERE t0.[Person_DocumentId] IS NOT NULL
    ) p
INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = p.[DocumentId]
ORDER BY
    doc.[DocumentId] ASC
;


