
CREATE TABLE IF NOT EXISTS dmscs.ClaimSet
(
    Id bigint NOT NULL GENERATED ALWAYS AS IDENTITY ( INCREMENT 1 START 1 MINVALUE 1 MAXVALUE 9223372036854775807 CACHE 1 ),
    ClaimSetName VARCHAR(256) NOT NULL,
    IsSystemReserved BOOLEAN NOT NULL,
	  ResourceClaims JSONB NOT NULL,
    CONSTRAINT claimset_pkey PRIMARY KEY (id)
);

ALTER TABLE dmscs.ClaimSet ADD CONSTRAINT uq_ClaimSetName UNIQUE (ClaimSetName);

CREATE UNIQUE INDEX idx_ClaimSetName ON dmscs.ClaimSet (ClaimSetName);

COMMENT ON COLUMN dmscs.claimset.id
    IS 'ClaimSet id';

COMMENT ON COLUMN dmscs.claimset.ClaimSetName
    IS 'Claim set name and must be unique';

COMMENT ON COLUMN dmscs.claimset.IsSystemReserved
    IS 'Is system reserved';

COMMENT ON COLUMN dmscs.claimset.ResourceClaims
    IS 'Contains a collection of Resource Claims in json format.';
