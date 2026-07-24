SELECT r."DocumentId"
FROM "dms"."Descriptor" r
INNER JOIN "dms"."Document" doc ON doc."DocumentId" = r."DocumentId"
WHERE
    (doc."DocumentUuid" = @id)
    AND (r."EffectiveEndDate" = @effectiveEndDate)
    AND (r."Namespace" = @namespace)
    AND (r."ResourceKeyId" = @resourceKeyId)
ORDER BY r."DocumentId" ASC
LIMIT @limit OFFSET @offset
;
