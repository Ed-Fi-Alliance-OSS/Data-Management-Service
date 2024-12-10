
CREATE TABLE IF NOT EXISTS dmscs.ClaimSet
(
    Id bigint NOT NULL GENERATED ALWAYS AS IDENTITY ( INCREMENT 1 START 1 MINVALUE 1 MAXVALUE 9223372036854775807 CACHE 1 ),
    ClaimSetName VARCHAR(256) NOT NULL,    
    IsSystemReserved BOOLEAN NOT NULL,
	  ResourceClaims JSONB NOT NULL,
    CONSTRAINT claimset_pkey PRIMARY KEY (id)
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS dmscs.claimset
    OWNER to postgres;

COMMENT ON COLUMN dmscs.claimset.id
    IS 'ClaimSet id';

COMMENT ON COLUMN dmscs.claimset.ClaimSetName
    IS 'Claim set name';

COMMENT ON COLUMN dmscs.claimset.IsSystemReserved
    IS 'Is system reserved';

COMMENT ON COLUMN dmscs.claimset.ResourceClaims
    IS 'Contains the Resource Claims information in json format.';
