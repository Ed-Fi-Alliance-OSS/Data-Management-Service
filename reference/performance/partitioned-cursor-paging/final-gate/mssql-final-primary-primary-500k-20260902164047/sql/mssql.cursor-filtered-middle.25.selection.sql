SELECT TOP (@pageSize) r.[DocumentId]
FROM [edfi].[Student] r
WHERE
    (r.[BirthDate] = @birthDate)
    AND (r.[DocumentId] >= @cursorMin)
    AND (r.[DocumentId] <= @cursorMax)
ORDER BY r.[DocumentId] ASC
;
