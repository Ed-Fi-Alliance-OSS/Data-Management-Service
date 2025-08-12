-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information
DO $$
DECLARE
    v_keyid TEXT := 'sample-key-id-001';
    v_encryptionKey TEXT := 'QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIzNDU2Nzg5MDEyMw==';
    -- This is the correct public key that matches the private key below
    v_publickey TEXT := 'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEApHfSK1Swfj6qViICMDBLoe6NUO4UWaVu4sx1/cnOMjSkhxyiJ5Kp4Z+TWWD6XMrhsxQ0Yiq4OB++7hmu+6D0ll8oUW4bKSBuTX/YlKbbIJaz7EFsjdpeZfBSxsSd4it3fdDL0R+BkGjPX1jwkZb3M6wBxjb96IMNpchOHmYyswa8cbRPtgvgyN0fBykcP5yW4NROPhQPy83tmxlXUfEFzCXCrG7yGFNqgyBg2D8G7LU6hMutMwbR+eeko615phWucVAVCTyuN0LHlafit8/OUZBxgfVic5e1U61VjNml3NY0/C9e7LlLAYPLsoAWO7L+kN4nCU6Ag1ZsYRoM7lnAuQIDAQAB';
    v_privatekey TEXT := 'MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCkd9IrVLB+PqpWIgIwMEuh7o1Q7hRZpW7izHX9yc4yNKSHHKInkqnhn5NZYPpcyuGzFDRiKrg4H77uGa77oPSWXyhRbhspIG5Nf9iUptsglrPsQWyN2l5l8FLGxJ3iK3d90MvRH4GQaM9fWPCRlvczrAHGNv3ogw2lyE4eZjKzBrxxtE+2C+DI3R8HKRw/nJbg1E4+FA/Lze2bGVdR8QXMJcKsbvIYU2qDIGDYPwbstTqEy60zBtH556SjrXmmFa5xUBUJPK43QseVp+K3z85RkHGB9WJzl7VTrVWM2aXc1jT8L17suUsBg8uygBY7sv6Q3icJToCDVmxhGgzuWcC5AgMBAAECggEBAIKtUahsGe+1CtJ1Ixf2x6FaUZ5EDJdOAtThb76+Yb8yZKeV8KFQvvouOH2DuGmSKdKH8zcsikLNtn6omYgFU1FHOlm5Coua4Qli00sJaIJ0O3E0anQrVWXZlWupPWk+8CpfhBIc3m1HWb2AhWSodrHvsVk0yHm951IZ3Tf6K75iDaob0SCbti7A55EljLQ9gVsTuzeBxcMpR9/3nkMNWxkbvqBUjoD674dZQ/9zZGvGQ6myqMFOBm7vtG0CflJtyikIc7OIIZr5F8t9EiGnaPoX+YS4uEYPWCrb1JVjxc5zgKk5SDlFqrRoFoHNYCpDIWuk4ObVS5avJL0SutNK4KECgYEA0KzHDeZ1shfp3zB8DUkt1rNyA49GP6G6/ahgcog9pI7RnDegG4yabEXB1eAJkXdlBHP3A/E90KCkXKR9b2tGvHck4FlORt3QwRSVfjgkKMrWx1F7qJFgQQRgJtyB2YyDgBfwulYOrhWS3x0o0VyfjngRhAgqYPkKDLIQxRBTnKsCgYEAycR8o5Bb+s14E1iZArjevEfeEnbQB3A0UE0G2tMBEOVGMHcm8X/CWeg3Mjy5ltB3qX/8aXGn7g1xhFhu34hEwX0qKPJ/ldyZJTLR+0HqIRgfyZdQFxolXwV1ibFdIZwlKqmD5W5xK+BnVTtLifnsFvEBb05vCQYYo0WEG/wLUCsCgYB9oLcJwEf1Gv56lrboTLki+89VI0l4f4aStW6zJSBvVGgO81IZo9FIA8sJVqKMB+QyBRqeLfs4Aa5R89lsXZotVlFGG53LfjjyNNE3NtdWE5+wSXb36eWX3umAG9q0vSph0Ifltm+KEITme6iaOnf4joKFCWFyFhwdvonoCcc8lwKBgGwvUrVQ/kCoUy3sX366KZPC5Sv5UOnsG+DCrF5ArV2l0dDC0rrCyi7y+EWTkd9vv/m+ilTvgB+ATdGsqSZqJpOozSZPgGGWevcbHMQgP62nBcRNwb/hYRBmGPPPiiQvWS5a3kHyyfPAyydEN+ivfQuABkjsQVURU7yX1ZI7vsUpAoGAT27PR/YVWY1GqHhgzTopEaZvtpaE9f7yY7N8Xw5jPLgGRxnzVCoyj8ySvZOqlQtHxDrWDHLzsMclBFyT+3PU0mSEgS0r66uFlAW+QtzhPyTFYvqSepJoq2ItbVEFClqEDlcELKyk5VB13Vx15CMnUPQl69Vja1SoqUhi2NJvTHw=';
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
