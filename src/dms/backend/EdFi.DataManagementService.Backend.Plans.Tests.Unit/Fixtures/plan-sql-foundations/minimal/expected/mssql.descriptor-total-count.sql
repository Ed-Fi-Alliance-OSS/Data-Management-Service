SELECT COUNT(1)
FROM [dms].[Descriptor] r
INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = r.[DocumentId]
WHERE
    (doc.[DocumentUuid] = @id)
    AND (r.[EffectiveEndDate] = @effectiveEndDate)
    AND (r.[Namespace] COLLATE Latin1_General_100_BIN2 = @namespace)
    AND (r.[ResourceKeyId] = @resourceKeyId)
;
