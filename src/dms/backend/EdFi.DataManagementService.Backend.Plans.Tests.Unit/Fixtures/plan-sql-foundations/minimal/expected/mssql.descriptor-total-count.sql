SELECT COUNT(1)
FROM [dms].[Document] r
INNER JOIN [dms].[Descriptor] d ON d.[DocumentId] = r.[DocumentId]
WHERE
    (d.[EffectiveEndDate] = @effectiveEndDate)
    AND (d.[Namespace] COLLATE Latin1_General_100_BIN2 = @namespace)
    AND (r.[DocumentUuid] = @id)
    AND (r.[ResourceKeyId] = @resourceKeyId)
;
