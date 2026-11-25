SELECT r."DocumentId", r."ContentVersion"
FROM "edfi"."School" r
WHERE
    (r."LocalEducationAgency_LocalEducationAgencyId" = @localEducationAgencyId)
    AND (EXISTS (SELECT 1 FROM "edfi"."SchoolIdentificationCode" ci WHERE ci."School_DocumentId" = r."DocumentId" AND ci."EducationOrganizationIdentificationSystemDescriptor_DescriptorId" = @educationOrganizationIdentificationSystemDescriptor AND ci."IdentificationCode" = @identificationCode))
    AND (r."ContentVersion" >= @cursorMin)
    AND (r."ContentVersion" <= @cursorMax)
ORDER BY r."ContentVersion" ASC
LIMIT @pageSize
;
