CREATE SCHEMA [edfi];

CREATE TABLE [edfi].[School] (
    [DocumentId] bigint NOT NULL,
    [SchoolId] int NOT NULL,
    CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])
);

CREATE TABLE [edfi].[SchoolAddress] (
    [DocumentId] bigint NOT NULL,
    [AddressOrdinal] int NOT NULL,
    [Street] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_SchoolAddress] PRIMARY KEY ([DocumentId], [AddressOrdinal])
);

CREATE TABLE [edfi].[SchoolAddressPhoneNumber] (
    [DocumentId] bigint NOT NULL,
    [AddressOrdinal] int NOT NULL,
    [PhoneNumberOrdinal] int NOT NULL,
    [PhoneNumber] nvarchar(20) NOT NULL,
    CONSTRAINT [PK_SchoolAddressPhoneNumber] PRIMARY KEY ([DocumentId], [AddressOrdinal], [PhoneNumberOrdinal])
);

ALTER TABLE [edfi].[SchoolAddress] ADD CONSTRAINT [FK_SchoolAddress_School] FOREIGN KEY ([DocumentId]) REFERENCES [edfi].[School] ([DocumentId]) ON DELETE CASCADE;

ALTER TABLE [edfi].[SchoolAddressPhoneNumber] ADD CONSTRAINT [FK_SchoolAddressPhoneNumber_SchoolAddress] FOREIGN KEY ([DocumentId], [AddressOrdinal]) REFERENCES [edfi].[SchoolAddress] ([DocumentId], [AddressOrdinal]) ON DELETE CASCADE;

