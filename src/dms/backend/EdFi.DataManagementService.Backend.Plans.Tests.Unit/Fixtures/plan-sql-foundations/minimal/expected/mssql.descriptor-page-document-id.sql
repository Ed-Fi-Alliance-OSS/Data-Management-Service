SELECT r.[DocumentId]
FROM [dms].[Descriptor] r
INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = r.[DocumentId]
WHERE
    (doc.[DocumentUuid] = @id)
    AND (r.[EffectiveEndDate] = @effectiveEndDate)
    AND (r.[Namespace] COLLATE Latin1_General_100_BIN2 = @namespace)
    AND (r.[ResourceKeyId] = @resourceKeyId)
ORDER BY r.[DocumentId] ASC
OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
;
