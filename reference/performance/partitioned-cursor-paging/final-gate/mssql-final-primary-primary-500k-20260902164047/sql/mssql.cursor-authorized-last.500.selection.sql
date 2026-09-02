SELECT TOP (@pageSize) r.[DocumentId]
FROM [edfi].[Student] r
WHERE
    ((r.[DocumentId] IN (SELECT t0.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t0 WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0) AND t0.[Student_DocumentId] >= @cursorMin AND t0.[Student_DocumentId] <= @cursorMax)))
    AND (r.[DocumentId] >= @cursorMin)
    AND (r.[DocumentId] <= @cursorMax)
ORDER BY r.[DocumentId] ASC
;
