SELECT r."DocumentId"
FROM "edfi"."StudentSchoolAssociation" r
WHERE
    (r."SchoolYear" >= @schoolYear)
    AND (r."Student_DocumentId" IS NOT NULL AND r."StudentUniqueId_Unified" = @studentUniqueId)
ORDER BY r."DocumentId" ASC
LIMIT @limit OFFSET @offset
;
