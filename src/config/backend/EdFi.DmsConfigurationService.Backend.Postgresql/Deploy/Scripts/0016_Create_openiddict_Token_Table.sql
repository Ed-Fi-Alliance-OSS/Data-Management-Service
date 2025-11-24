-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.OpenIddictToken (
    Id uuid NOT NULL PRIMARY KEY,
    ApplicationId uuid,
    AuthorizationId uuid,
    CreationDate timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
    Payload TEXT,
    Properties varchar(200),
    RedemptionDate timestamp without time zone,
    Subject varchar(100),
    Type varchar(50),
    ReferenceId varchar(100),
    ExpirationDate timestamp without time zone,
    Status varchar(50) DEFAULT 'valid',
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256)
);

COMMENT ON TABLE dmscs.OpenIddictToken IS 'OpenIddict tokens storage.';

COMMENT ON COLUMN dmscs.OpenIddictToken.Id IS 'Token identifier.';

COMMENT ON COLUMN dmscs.OpenIddictToken.ApplicationId IS 'Associated application id.';

COMMENT ON COLUMN dmscs.OpenIddictToken.Subject IS 'Token subject (user or client id).';

COMMENT ON COLUMN dmscs.OpenIddictToken.Type IS 'Token type (access, refresh, etc).';

COMMENT ON COLUMN dmscs.OpenIddictToken.ReferenceId IS 'Reference id for the token.';


COMMENT ON COLUMN dmscs.OpenIddictToken.ExpirationDate IS 'Token expiration date.';

COMMENT ON COLUMN dmscs.OpenIddictToken.AuthorizationId IS 'Associated authorization identifier.';
COMMENT ON COLUMN dmscs.OpenIddictToken.CreationDate IS 'Token creation timestamp.';
COMMENT ON COLUMN dmscs.OpenIddictToken.Payload IS 'Token payload (JWT content).';
COMMENT ON COLUMN dmscs.OpenIddictToken.Properties IS 'Additional token properties (JSON).';
COMMENT ON COLUMN dmscs.OpenIddictToken.RedemptionDate IS 'Token redemption timestamp.';
COMMENT ON COLUMN dmscs.OpenIddictToken.Status IS 'Token status (valid, revoked, expired).';
COMMENT ON COLUMN dmscs.OpenIddictToken.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.OpenIddictToken.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.OpenIddictToken.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.OpenIddictToken.ModifiedBy IS 'User or client ID who last modified the record';


-- Add index for better performance
CREATE INDEX IF NOT EXISTS idx_OpenIddictToken_ApplicationId ON dmscs.OpenIddictToken(ApplicationId);
CREATE INDEX IF NOT EXISTS idx_OpenIddictToken_Subject ON dmscs.OpenIddictToken(Subject);
CREATE INDEX IF NOT EXISTS idx_OpenIddictToken_ReferenceId ON dmscs.OpenIddictToken(ReferenceId);
CREATE INDEX IF NOT EXISTS idx_OpenIddictToken_ExpirationDate ON dmscs.OpenIddictToken(ExpirationDate);
