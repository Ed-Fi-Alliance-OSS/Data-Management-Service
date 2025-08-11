-- Create table for OpenID keys
CREATE TABLE IF NOT EXISTS dmscs.OpenIddictKey (
    Id SERIAL PRIMARY KEY,
    KeyId VARCHAR(64) NOT NULL,
    PublicKey TEXT NOT NULL, -- base64-encoded
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    ExpiresAt TIMESTAMP,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE
);

-- Insert initial key (replace with your actual base64 public key)
INSERT INTO dmscs.OpenIddictKey (KeyId, PublicKey, IsActive)
VALUES ('initial-key-id', 'BASE64_PUBLIC_KEY_HERE', TRUE);
