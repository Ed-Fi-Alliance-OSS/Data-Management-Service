
ALTER TABLE dmscs.openiddict_token
ADD COLUMN IF NOT EXISTS authorization_id uuid,
ADD COLUMN IF NOT EXISTS creation_date timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
ADD COLUMN IF NOT EXISTS payload text,
ADD COLUMN IF NOT EXISTS properties text,
ADD COLUMN IF NOT EXISTS redemption_date timestamp without time zone,
ADD COLUMN IF NOT EXISTS status text DEFAULT 'valid';

COMMENT ON COLUMN dmscs.openiddict_token.authorization_id IS 'Associated authorization identifier.';
COMMENT ON COLUMN dmscs.openiddict_token.creation_date IS 'Token creation timestamp.';
COMMENT ON COLUMN dmscs.openiddict_token.payload IS 'Token payload (JWT content).';
COMMENT ON COLUMN dmscs.openiddict_token.properties IS 'Additional token properties (JSON).';
COMMENT ON COLUMN dmscs.openiddict_token.redemption_date IS 'Token redemption timestamp.';
COMMENT ON COLUMN dmscs.openiddict_token.status IS 'Token status (valid, revoked, expired).';

CREATE INDEX IF NOT EXISTS idx_openiddict_token_application_id ON dmscs.openiddict_token(application_id);
CREATE INDEX IF NOT EXISTS idx_openiddict_token_subject ON dmscs.openiddict_token(subject);
CREATE INDEX IF NOT EXISTS idx_openiddict_token_reference_id ON dmscs.openiddict_token(reference_id);
CREATE INDEX IF NOT EXISTS idx_openiddict_token_expiration ON dmscs.openiddict_token(expiration_date);


