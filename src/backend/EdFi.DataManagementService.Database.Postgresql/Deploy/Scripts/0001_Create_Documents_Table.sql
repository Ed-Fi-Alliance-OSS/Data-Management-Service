CREATE TABLE IF NOT EXISTS Documents (
  id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  document_partition_key SMALLINT NOT NULL,
  document_uuid CHAR(36) NOT NULL,
  resource_name VARCHAR(256) NOT NULL,
  edfi_doc BYTEA NOT NULL,
  PRIMARY KEY (document_partition_key, id)
);
