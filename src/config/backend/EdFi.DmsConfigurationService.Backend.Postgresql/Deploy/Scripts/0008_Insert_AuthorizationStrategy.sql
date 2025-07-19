-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

ALTER TABLE dmscs.AuthorizationStrategy ALTER COLUMN Id DROP IDENTITY;

INSERT INTO dmscs.AuthorizationStrategy (Id, AuthorizationStrategyName, DisplayName)
SELECT v.Id, v.AuthorizationStrategyName, v.DisplayName FROM (
    VALUES
        (1, 'NoFurtherAuthorizationRequired', 'No Further Authorization Required'),
        (2, 'RelationshipsWithEdOrgsAndPeople', 'Relationships with Education Organizations and People'),
        (3, 'RelationshipsWithEdOrgsOnly', 'Relationships with Education Organizations only'),
        (4, 'NamespaceBased', 'Namespace Based'),
        (5, 'RelationshipsWithPeopleOnly', 'Relationships with People only'),
        (6, 'RelationshipsWithStudentsOnly', 'Relationships with Students only'),
        (7, 'RelationshipsWithStudentsOnlyThroughResponsibility', 'Relationships with Students only (through StudentEducationOrganizationResponsibilityAssociation)'),
        (8, 'OwnershipBased', 'Ownership Based'),
        (9, 'RelationshipsWithEdOrgsAndPeopleIncludingDeletes', 'Relationships with Education Organizations and People (including deletes)'),
        (10, 'RelationshipsWithStudentsOnlyIncludingDeletes', 'Relationships With Students Only Including Deletes'),
        (11, 'RelationshipsWithEdOrgsOnlyInverted', 'Relationships with Education Organizations only (Inverted)'),
        (12, 'RelationshipsWithEdOrgsAndPeopleInverted', 'Relationships with Education Organizations and People (Inverted)'),
        (13, 'RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes', 'Relationships with Students only (through StudentEducationOrganizationResponsibilityAssociation, including deletes)')
) AS v(Id, AuthorizationStrategyName, DisplayName)
WHERE NOT EXISTS (
    SELECT 1 FROM dmscs.AuthorizationStrategy s WHERE s.Id = v.Id
);

ALTER TABLE dmscs.AuthorizationStrategy ALTER COLUMN Id ADD GENERATED ALWAYS AS IDENTITY;

DO $$
DECLARE
    seq_name text := 'dmscs.authorizationstrategy_id_seq';
    max_id bigint;
BEGIN
    SELECT MAX(Id) INTO max_id FROM dmscs.AuthorizationStrategy;
    IF max_id IS NOT NULL THEN
        EXECUTE format('SELECT setval(%L, %s)', seq_name, max_id);
    END IF;
END$$;
