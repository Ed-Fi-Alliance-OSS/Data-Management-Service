-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- To improve GetByQuery requests performance
CREATE INDEX IF NOT EXISTS IX_Document_ResourceName_CreatedAt ON dms.Document (ResourceName, CreatedAt);

-- To improve GetByQuery authorization performance
CREATE INDEX IF NOT EXISTS IX_Document_StudentSchoolAuthorizationEdOrgIds ON dms.Document USING GIN (StudentSchoolAuthorizationEdOrgIds);
CREATE INDEX IF NOT EXISTS IX_Document_ContactStudentSchoolAuthorizationEdOrgIds ON dms.Document USING GIN (ContactStudentSchoolAuthorizationEdOrgIds);
CREATE INDEX IF NOT EXISTS IX_Document_StaffEducationOrganizationAuthorizationEdOrgIds ON dms.Document USING GIN (StaffEducationOrganizationAuthorizationEdOrgIds);
CREATE INDEX IF NOT EXISTS IX_Document_StudentEdOrgResponsibilityAuthorizationIds ON dms.Document USING GIN (StudentEdOrgResponsibilityAuthorizationIds);

CREATE INDEX IF NOT EXISTS IX_Document_SecurityElements_EducationOrganization ON dms.Document USING GIN ((SecurityElements -> 'EducationOrganization'));
CREATE INDEX IF NOT EXISTS IX_Document_SecurityElements_Namespace ON dms.Document USING GIN ((SecurityElements -> 'Namespace'));
