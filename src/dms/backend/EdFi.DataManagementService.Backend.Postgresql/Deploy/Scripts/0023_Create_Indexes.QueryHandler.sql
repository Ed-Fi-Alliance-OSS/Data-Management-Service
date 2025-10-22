-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Speeds up the retrieval of resources when using offset/limit paging (GetAll)
CREATE INDEX IF NOT EXISTS IX_Document_ResourceName_CreatedAt ON dms.Document (ResourceName, CreatedAt);

-- The next indexes speed up authorization checks:
-- CREATE INDEX IF NOT EXISTS IX_Document_StudentSchoolAuthorizationEdOrgIds ON dms.Document USING GIN (StudentSchoolAuthorizationEdOrgIds);
-- CREATE INDEX IF NOT EXISTS IX_Document_ContactStudentSchoolAuthorizationEdOrgIds ON dms.Document USING GIN (ContactStudentSchoolAuthorizationEdOrgIds);
-- CREATE INDEX IF NOT EXISTS IX_Document_StaffEducationOrganizationAuthorizationEdOrgIds ON dms.Document USING GIN (StaffEducationOrganizationAuthorizationEdOrgIds);
-- CREATE INDEX IF NOT EXISTS IX_Document_StudentEdOrgResponsibilityAuthorizationIds ON dms.Document USING GIN (StudentEdOrgResponsibilityAuthorizationIds);

-- CREATE INDEX IF NOT EXISTS IX_Document_SecurityElements_EducationOrganization ON dms.Document ((SecurityElements->'EducationOrganization'->0->>'Id'));
-- CREATE INDEX IF NOT EXISTS IX_Document_SecurityElements_Namespace ON dms.Document ((SecurityElements->'Namespace'->>0) text_pattern_ops);

-- Speeds up the retrieval of resources filtered by any of its fields (GetByQuery)
CREATE INDEX IF NOT EXISTS IX_Document_EdfiDoc ON dms.Document USING GIN (EdfiDoc jsonb_path_ops);
