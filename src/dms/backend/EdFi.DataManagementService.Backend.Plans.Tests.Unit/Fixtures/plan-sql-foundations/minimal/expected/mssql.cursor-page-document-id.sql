SELECT TOP (@pageSize) r.[DocumentId]
FROM [edfi].[StudentSchoolAssociation] r
WHERE
    (r.[SchoolYear] >= @schoolYear)
    AND (r.[Student_DocumentId] IS NOT NULL AND r.[StudentUniqueId_Unified] COLLATE Latin1_General_100_BIN2 = @studentUniqueId)
    AND (r.[DocumentId] >= @cursorMin)
    AND (r.[DocumentId] <= @cursorMax)
ORDER BY r.[DocumentId] ASC
;
