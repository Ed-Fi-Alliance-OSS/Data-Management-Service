SELECT r.[DocumentId]
FROM [dms].[Document] r
INNER JOIN [dms].[Descriptor] d ON d.[DocumentId] = r.[DocumentId]
WHERE
    (d.[EffectiveEndDate] = @effectiveEndDate)
    AND (d.[Namespace] COLLATE Latin1_General_100_BIN2 = @namespace)
    AND (r.[DocumentUuid] = @id)
    AND (r.[ResourceKeyId] = @resourceKeyId)
ORDER BY r.[DocumentId] ASC
OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
;
