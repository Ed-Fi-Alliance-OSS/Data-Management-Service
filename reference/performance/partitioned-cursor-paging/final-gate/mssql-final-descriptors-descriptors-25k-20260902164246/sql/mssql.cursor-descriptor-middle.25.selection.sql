SELECT
    page_document_ids.[DocumentId] AS [DocumentId],
    document.[DocumentUuid] AS [DocumentUuid],
    document.[ContentVersion] AS [ContentVersion],
    document.[ContentLastModifiedAt] AS [ContentLastModifiedAt],
    document.[ResourceKeyId] AS [ResourceKeyId],
    descriptor.[Namespace] AS [Namespace],
    descriptor.[CodeValue] AS [CodeValue],
    descriptor.[ShortDescription] AS [ShortDescription],
    descriptor.[Description] AS [Description],
    descriptor.[EffectiveBeginDate] AS [EffectiveBeginDate],
    descriptor.[EffectiveEndDate] AS [EffectiveEndDate],
    descriptor.[Discriminator] AS [Discriminator]
FROM (
SELECT TOP (@pageSize) r.[DocumentId]
FROM [dms].[Descriptor] r
WHERE
    (r.[ResourceKeyId] = @resourceKeyId)
    AND (r.[Namespace] IS NOT NULL AND (r.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\'))
    AND (r.[DocumentId] >= @cursorMin)
    AND (r.[DocumentId] <= @cursorMax)
ORDER BY r.[DocumentId] ASC
) page_document_ids
INNER JOIN [dms].[Document] document
    ON document.[DocumentId] = page_document_ids.[DocumentId]
LEFT JOIN [dms].[Descriptor] descriptor
    ON descriptor.[DocumentId] = page_document_ids.[DocumentId]
ORDER BY page_document_ids.[DocumentId] ASC;