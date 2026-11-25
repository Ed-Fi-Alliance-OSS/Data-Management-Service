SELECT r.[DocumentId]
FROM [edfi].[School] r
WHERE
    (r.[LocalEducationAgency_LocalEducationAgencyId] = @localEducationAgencyId)
    AND (EXISTS (SELECT 1 FROM [edfi].[SchoolIdentificationCode] ci WHERE ci.[School_DocumentId] = r.[DocumentId] AND ci.[EducationOrganizationIdentificationSystemDescriptor_DescriptorId] = @educationOrganizationIdentificationSystemDescriptor AND ci.[IdentificationCode] = @identificationCode))
ORDER BY r.[DocumentId] ASC
OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
;
