-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information
DO $$
DECLARE
    v_keyid TEXT := 'sample-key-id-001';
    v_encryptionKey TEXT := 'QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIzNDU2Nzg5MDEyMw==';
    -- This is the correct public key that matches the private key below
    v_publickey TEXT := 'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA1/3QW79ZA8nfZS2XMvAsDg5n87alrhA29HVYGgzzeNlPuNKjrLiMiX8GJDLSJ/eYf9KOyWyMkHzCclN7ZoUTFDc1lTBk0oJL+pRPvbt+ORrW7oEWD4sq4NxUGVGbxsUf0R6FH8VScmWtoyoIqb0vxl6QLX4RccNLJVylD8/N4fNGKLFsVfPRxZlzuU0kPvdcosIKnuTBZWTt+b5DRu5ZJwD8BWVrUgm/1p/JM2wkA0jYgRAs/IVWMbq/VxYJOnXKEehjyDcc8yyGJuKn5J7MQuy8BlgtFndB2J+B2sWyNiYXVrz2SYOLQ8DU91pmUc4WF2f1GNkdZ8IdMZ9ZcBt0dQIDAQAB';
    v_privatekey TEXT := 'MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQDX/dBbv1kDyd9lLZcy8CwODmfztqWuEDb0dVgaDPN42U+40qOsuIyJfwYkMtIn95h/0o7JbIyQfMJyU3tmhRMUNzWVMGTSgkv6lE+9u345GtbugRYPiyrg3FQZUZvGxR/RHoUfxVJyZa2jKgipvS/GXpAtfhFxw0slXKUPz83h80YosWxV89HFmXO5TSQ+91yiwgqe5MFlZO35vkNG7lknAPwFZWtSCb/Wn8kzbCQDSNiBECz8hVYxur9XFgk6dcoR6GPINxzzLIYm4qfknsxC7LwGWC0Wd0HYn4HaxbI2JhdWvPZJg4tDwNT3WmZRzhYXZ/UY2R1nwh0xn1lwG3R1AgMBAAECggEAWFGS11FA5smvLUIdJ1kJyp2daAxxZuF+dytcYRqWm/3QGXUYNFIqNTbZngeh43Hcy7efZ0GZoKNDJ1h3hw43JPcGVAC72VAqHUZz7NMz48nTxSbHjIeNevDc+pViKz8DqZDfQoR/GAP3olZXwIB5fpXAQrngDDKdEaP2YqbIOvFsg6CaYDCI6GtWK1ybgXdbfHeFAQtzt7yOiR8yGNBlALszPWfLFJBd1YiwYJZdlGJkrvg9eMX7kJxqsK+0zi27j+ud1bxt2+6hClItXemHKKFCxWe3i/sJzyHLDk5xbpME5VK+qfW82tuXgFrWMAoEPPs27BeKv2JB19bhqZIRqQKBgQDkDo/CRcEWoeDWwixHGwv1Hc1LjA5mRJh4MFJC4Y98fFjykEcO7RgpVgstUPn+kwMN/TKbDypqXaOUSU+QffsytcROqEF37FxPLPk/MRj2q2Sp6AOguXHMorcS8KzFqJY/9Xm/VMcgHwAHSL6PWgaRlmqo9zYU3O2CfsLcdqNWwwKBgQDydMwlcQ5wPDtPnjO7b9GtwRxlbFz87LaelLr5RPZV2DjXQrAxDwec+ukdfJ+Y++E1GArFN4Kog0Dr10gNfvPlqMNye4M0QpdO3l5XEM7xQXl6xjWMpyClxrI/Q2wYM0NYqpNJooT8CXwYXhfevFHhQOT1F24LcAPq+xMd4U2EZwKBgGbPrb2ORssWNU98AAwaRFy/j7KUNFWkbPwaBKvEFjSvtkW8B1zSREc2VBmc3OcIjaL715mRz7Rd/IW4OxdPxDQLP7GaJtGSi9bh1ofHcZKal+oE/8WwdH4liNUQDUOaignRd45rAM4ZS6D9CXOEyVtO7Uy5Dfd/1c8zqFNNZLuTAoGAQAn1edY4uBBQoiDpDRLl0Pz2oRtUHEHxokUqdXhvkBECQmkM3IhZvG7Rb8Zg6SluPHXTMnANBLFWTnSYRWhIx1oh9XUGHKGSEXTOejSoVDS0/2am8jWae+7VWbxXKrUvjpXPPV29vkxLCKyhpWUcQ2C+mLXNjRvTDRev3u6JaPUCgYAT8JlV3G1pCh71LC7sUBZSh2Sh/3Za3078NmMlkModwaH1PKR+XEd/rHxnYDfZ+vmuZ2sMepnsTiVS0wSpCwgJXa8hemol/9tn02OYyYk1Q6CtTYLSjMexOf3BG43UWD8FQUJhqCzzhq4MT40SiSwL27clXM81r1td/GJHTtbBVA==';
BEGIN
    -- Update table schema to use bytea for binary storage
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'dmscs'
        AND table_name = 'openiddictkey'
        AND column_name = 'publickey'
        AND data_type = 'bytea'
    ) THEN
        ALTER TABLE dmscs.OpenIddictKey
        ALTER COLUMN PublicKey TYPE bytea USING PublicKey::bytea;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM dmscs.OpenIddictKey WHERE KeyId = v_keyid
    ) THEN
        INSERT INTO dmscs.OpenIddictKey (KeyId, PublicKey, PrivateKey, IsActive)
        VALUES (v_keyid, decode(v_publickey, 'base64'), pgp_sym_encrypt(v_privatekey, v_encryptionKey), TRUE);
    ELSE
        -- Update existing key
        UPDATE dmscs.OpenIddictKey
        SET PublicKey = decode(v_publickey, 'base64'),
            PrivateKey = pgp_sym_encrypt(v_privatekey, v_encryptionKey),
            IsActive = TRUE
        WHERE KeyId = v_keyid;
    END IF;
END $$;
