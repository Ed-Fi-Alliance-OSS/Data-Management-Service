-- Extended Properties [testextension].[HairColorDescriptor] --
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'This descriptor defines student hair color.', @level0type=N'SCHEMA', @level0name=N'testextension', @level1type=N'TABLE', @level1name=N'HairColorDescriptor'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'A unique identifier used as Primary Key, not derived from business logic, when acting as Foreign Key, references the parent table.', @level0type=N'SCHEMA', @level0name=N'testextension', @level1type=N'TABLE', @level1name=N'HairColorDescriptor', @level2type=N'COLUMN', @level2name=N'HairColorDescriptorId'
GO

-- Extended Properties [testextension].[StudentEducationOrganizationAssociationExtension] --
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'', @level0type=N'SCHEMA', @level0name=N'testextension', @level1type=N'TABLE', @level1name=N'StudentEducationOrganizationAssociationExtension'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'The identifier assigned to an education organization.', @level0type=N'SCHEMA', @level0name=N'testextension', @level1type=N'TABLE', @level1name=N'StudentEducationOrganizationAssociationExtension', @level2type=N'COLUMN', @level2name=N'EducationOrganizationId'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'A unique alphanumeric code assigned to a student.', @level0type=N'SCHEMA', @level0name=N'testextension', @level1type=N'TABLE', @level1name=N'StudentEducationOrganizationAssociationExtension', @level2type=N'COLUMN', @level2name=N'StudentUSI'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'hair color of student', @level0type=N'SCHEMA', @level0name=N'testextension', @level1type=N'TABLE', @level1name=N'StudentEducationOrganizationAssociationExtension', @level2type=N'COLUMN', @level2name=N'HairColorDescriptorId'
GO

