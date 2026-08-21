SELECT r.[DocumentId]
FROM [edfi].[Student] r
ORDER BY r.[DocumentId] ASC
OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
;
