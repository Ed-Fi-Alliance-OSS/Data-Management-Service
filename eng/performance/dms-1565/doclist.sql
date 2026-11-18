-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
--
-- DMS-1565 document selection: deterministic by DocumentId, documents with dependents first where noted.
-- Run against a fresh restore of the v81 backup; make-doclist.sh converts the output to doclist.json.
SET NOCOUNT ON;
SELECT 'sessions' AS r, d.DocumentUuid, s.DocumentId FROM edfi.Session s JOIN dms.Document d ON d.DocumentId = s.DocumentId ORDER BY s.DocumentId;
SELECT 'classPeriods' AS r, d.DocumentUuid, c.DocumentId FROM edfi.ClassPeriod c JOIN dms.Document d ON d.DocumentId = c.DocumentId ORDER BY c.DocumentId;
SELECT TOP 200 'courseOfferings' AS r, d.DocumentId AS x, d.DocumentUuid, co.DocumentId FROM edfi.CourseOffering co JOIN dms.Document d ON d.DocumentId = co.DocumentId
  ORDER BY CASE WHEN EXISTS (SELECT 1 FROM edfi.Section se WHERE se.CourseOffering_DocumentId = co.DocumentId) THEN 0 ELSE 1 END, co.DocumentId;
SELECT TOP 200 'sections' AS r, d.DocumentId AS x, d.DocumentUuid, se.DocumentId FROM edfi.Section se JOIN dms.Document d ON d.DocumentId = se.DocumentId
  ORDER BY CASE WHEN EXISTS (SELECT 1 FROM edfi.StudentSectionAssociation ssa WHERE ssa.Section_DocumentId = se.DocumentId) THEN 0 ELSE 1 END, se.DocumentId;
SELECT TOP 200 'studentSchoolAssociations' AS r, d.DocumentUuid, a.DocumentId FROM edfi.StudentSchoolAssociation a JOIN dms.Document d ON d.DocumentId = a.DocumentId ORDER BY a.DocumentId;
