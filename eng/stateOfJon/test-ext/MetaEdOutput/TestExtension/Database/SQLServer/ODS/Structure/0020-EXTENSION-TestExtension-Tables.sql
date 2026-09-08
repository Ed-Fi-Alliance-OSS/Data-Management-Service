-- Table [testextension].[HairColorDescriptor] --
CREATE TABLE [testextension].[HairColorDescriptor] (
    [HairColorDescriptorId] [INT] NOT NULL,
    CONSTRAINT [HairColorDescriptor_PK] PRIMARY KEY CLUSTERED (
        [HairColorDescriptorId] ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

-- Table [testextension].[StudentEducationOrganizationAssociationExtension] --
CREATE TABLE [testextension].[StudentEducationOrganizationAssociationExtension] (
    [EducationOrganizationId] [BIGINT] NOT NULL,
    [StudentUSI] [INT] NOT NULL,
    [HairColorDescriptorId] [INT] NULL,
    [CreateDate] [DATETIME2] NOT NULL,
    CONSTRAINT [StudentEducationOrganizationAssociationExtension_PK] PRIMARY KEY CLUSTERED (
        [EducationOrganizationId] ASC,
        [StudentUSI] ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
ALTER TABLE [testextension].[StudentEducationOrganizationAssociationExtension] ADD CONSTRAINT [StudentEducationOrganizationAssociationExtension_DF_CreateDate] DEFAULT (getutcdate()) FOR [CreateDate]
GO

