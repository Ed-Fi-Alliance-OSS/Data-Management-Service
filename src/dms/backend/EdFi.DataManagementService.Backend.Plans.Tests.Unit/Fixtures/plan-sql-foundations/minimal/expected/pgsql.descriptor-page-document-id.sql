SELECT r."DocumentId"
FROM "dms"."Document" r
INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = r."DocumentId"
WHERE
    (d."EffectiveEndDate" = @effectiveEndDate)
    AND (d."Namespace" = @namespace)
    AND (r."DocumentUuid" = @id)
    AND (r."ResourceKeyId" = @resourceKeyId)
ORDER BY r."DocumentId" ASC
LIMIT @limit OFFSET @offset
;
