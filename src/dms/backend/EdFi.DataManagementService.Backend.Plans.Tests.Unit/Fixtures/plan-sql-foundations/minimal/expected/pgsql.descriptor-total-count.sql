SELECT COUNT(1)
FROM "dms"."Descriptor" r
WHERE
    (r."DocumentUuid" = @id)
    AND (r."EffectiveEndDate" = @effectiveEndDate)
    AND (r."Namespace" = @namespace)
    AND (r."ResourceKeyId" = @resourceKeyId)
;
