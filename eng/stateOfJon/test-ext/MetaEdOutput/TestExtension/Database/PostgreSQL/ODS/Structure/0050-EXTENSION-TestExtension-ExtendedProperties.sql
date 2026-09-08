-- Extended Properties [testextension].[HairColorDescriptor] --
COMMENT ON TABLE testextension.HairColorDescriptor IS 'This descriptor defines student hair color.';
COMMENT ON COLUMN testextension.HairColorDescriptor.HairColorDescriptorId IS 'A unique identifier used as Primary Key, not derived from business logic, when acting as Foreign Key, references the parent table.';

-- Extended Properties [testextension].[StudentEducationOrganizationAssociationExtension] --
COMMENT ON TABLE testextension.StudentEducationOrganizationAssociationExtension IS '';
COMMENT ON COLUMN testextension.StudentEducationOrganizationAssociationExtension.EducationOrganizationId IS 'The identifier assigned to an education organization.';
COMMENT ON COLUMN testextension.StudentEducationOrganizationAssociationExtension.StudentUSI IS 'A unique alphanumeric code assigned to a student.';
COMMENT ON COLUMN testextension.StudentEducationOrganizationAssociationExtension.HairColorDescriptorId IS 'hair color of student';

