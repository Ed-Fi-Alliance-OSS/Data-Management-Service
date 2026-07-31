SELECT r."DocumentId"
FROM "dms"."Descriptor" r
WHERE
    (r."DocumentUuid" = @id)
    AND (r."EffectiveEndDate" = @effectiveEndDate)
    AND (r."Namespace" = @namespace)
    AND (r."ResourceKeyId" = @resourceKeyId)
ORDER BY r."DocumentId" ASC
LIMIT @limit OFFSET @offset
;
