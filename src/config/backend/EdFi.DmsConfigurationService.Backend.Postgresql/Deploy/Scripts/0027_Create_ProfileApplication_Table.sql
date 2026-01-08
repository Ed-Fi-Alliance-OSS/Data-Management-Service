-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.
-- ============================================================================

DO $$
BEGIN
	IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'dmscs') THEN
		EXECUTE 'CREATE SCHEMA dmscs';
	END IF;
END$$;

CREATE TABLE IF NOT EXISTS dmscs.ApplicationProfile (
	ApplicationId BIGINT NOT NULL,
	ProfileId BIGINT NOT NULL,
	CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
	CreatedBy VARCHAR(256),
	PRIMARY KEY (ApplicationId, ProfileId),
	CONSTRAINT fk_applicationprofile_application
		FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE,
	CONSTRAINT fk_applicationprofile_profile
		FOREIGN KEY (ProfileId) REFERENCES dmscs.Profile(Id) ON DELETE RESTRICT
);
