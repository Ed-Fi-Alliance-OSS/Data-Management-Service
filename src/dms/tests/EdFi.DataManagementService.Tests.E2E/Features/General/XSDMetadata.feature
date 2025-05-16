Feature: XSD Metadata Endpoint

        Scenario: 01 Ensure clients can retrieve XSD endpoint information
             When a GET request is made to "/metadata/xsd"
             Then it should respond with 200
              And the general response body is
                  """
                    [
                      {
                        "description": "Core schema (Ed-Fi) files for the data model",
                        "name": "ed-fi",
                        "version": "5.2.0",
                        "files": "http://localhost:8080/metadata/xsd/ed-fi/files"
                      },
                      {
                        "description": "Extension (Homograph) blended with Core schema files for the data model",
                        "name": "homograph",
                        "version": "1.0.0",
                        "files": "http://localhost:8080/metadata/xsd/homograph/files"
                      },
                      {
                        "description": "Extension (Sample) blended with Core schema files for the data model",
                        "name": "sample",
                        "version": "1.0.0",
                        "files": "http://localhost:8080/metadata/xsd/sample/files"
                      },
                      {
                        "description": "Extension (TPDM) blended with Core schema files for the data model",
                        "name": "tpdm",
                        "version": "1.1.0",
                        "files": "http://localhost:8080/metadata/xsd/tpdm/files"
                      }
                    ]
                  """
        Scenario: 02 Ensure clients can retrieve Core schema (Ed-Fi) files for the data model
             When a GET request is made to "/metadata/xsd/ed-fi/files"
             Then it should respond with 200
              And the general response body is
                  """
                    [
                      "http://localhost:8080/metadata/xsd/ed-fi/Ed-Fi-Core.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-AssessmentMetadata.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-AssessmentRegistration.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-Contact.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-Descriptors.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-EducationOrganization.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-EducationOrgCalendar.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-Finance.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-MasterSchedule.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-PostSecondaryEvent.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StaffAssociation.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-Standards.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-Student.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentAssessment.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentAttendance.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentCohort.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentDiscipline.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentEnrollment.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentGrade.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentGradebook.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentHealth.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentIntervention.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentProgram.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentProgramEvaluation.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-StudentTranscript.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/Interchange-Survey.xsd",
                      "http://localhost:8080/metadata/xsd/ed-fi/SchemaAnnotation.xsd"
                    ]
                  """
        Scenario: 03 Ensure clients can retrieve Extension (Sample) blended with Core schema files for the data model
             When a GET request is made to "/metadata/xsd/sample/files"
             Then it should respond with 200
              And the general response body is
                  """
                    [
                      "http://localhost:8080/metadata/xsd/sample/Ed-Fi-Core.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-AssessmentMetadata.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-AssessmentRegistration.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-Contact.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-Descriptors.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-EducationOrganization.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-EducationOrgCalendar.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-Finance.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-MasterSchedule.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-PostSecondaryEvent.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StaffAssociation.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-Standards.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-Student.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentAssessment.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentAttendance.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentCohort.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentDiscipline.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentEnrollment.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentGrade.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentGradebook.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentHealth.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentIntervention.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentProgram.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentProgramEvaluation.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-StudentTranscript.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Interchange-Survey.xsd",
                      "http://localhost:8080/metadata/xsd/sample/SchemaAnnotation.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Ed-Fi-Extended-Core.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-Contact-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-Descriptors-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-EducationOrganization-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-StaffAssociation-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-Student-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-StudentEnrollment-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-StudentHealth-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/sample/Sample-EXTENSION-Interchange-StudentProgram-Extension.xsd"
                    ]
                  """
        Scenario: 04 Ensure clients can retrieve Extension (Homograph) blended with Core schema files for the data model
             When a GET request is made to "/metadata/xsd/homograph/files"
             Then it should respond with 200
              And the general response body is
                  """
                    [
                      "http://localhost:8080/metadata/xsd/homograph/Ed-Fi-Core.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-AssessmentMetadata.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-AssessmentRegistration.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-Contact.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-Descriptors.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-EducationOrganization.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-EducationOrgCalendar.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-Finance.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-MasterSchedule.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-PostSecondaryEvent.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StaffAssociation.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-Standards.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-Student.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentAssessment.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentAttendance.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentCohort.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentDiscipline.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentEnrollment.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentGrade.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentGradebook.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentHealth.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentIntervention.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentProgram.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentProgramEvaluation.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-StudentTranscript.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/Interchange-Survey.xsd",
                      "http://localhost:8080/metadata/xsd/homograph/SchemaAnnotation.xsd"
                    ]
                  """
        Scenario: 05 Ensure clients can retrieve Extension (TPDM) blended with Core schema files for the data model
             When a GET request is made to "/metadata/xsd/tpdm/files"
             Then it should respond with 200
              And the general response body is
                  """
                    [
                      "http://localhost:8080/metadata/xsd/tpdm/Ed-Fi-Core.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-AssessmentMetadata.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-AssessmentRegistration.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-Contact.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-Descriptors.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-EducationOrganization.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-EducationOrgCalendar.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-Finance.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-MasterSchedule.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-PostSecondaryEvent.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StaffAssociation.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-Standards.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-Student.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentAssessment.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentAttendance.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentCohort.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentDiscipline.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentEnrollment.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentGrade.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentGradebook.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentHealth.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentIntervention.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentProgram.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentProgramEvaluation.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-StudentTranscript.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/Interchange-Survey.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/SchemaAnnotation.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/TPDM-EXTENSION-Ed-Fi-Extended-Core.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/TPDM-EXTENSION-Interchange-Candidate-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/TPDM-EXTENSION-Interchange-Descriptors-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/TPDM-EXTENSION-Interchange-EducationOrganization-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/TPDM-EXTENSION-Interchange-PerformanceEvaluation-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/TPDM-EXTENSION-Interchange-StaffAssociation-Extension.xsd",
                      "http://localhost:8080/metadata/xsd/tpdm/TPDM-EXTENSION-Interchange-Survey-Extension.xsd"
                    ]
                  """
        Scenario: 06 Ensure clients can retrieve XSD content of TPDM Extension
             When a GET request is made to "/metadata/xsd/tpdm/EdFi.TPDM.ApiSchema.xsd.TPDM-EXTENSION-Interchange-Survey-Extension.xsd"
             Then it should respond with 200
              And the xsd response body is
                  """
                    <?xml version="1.0" encoding="UTF-8" ?>
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns="http://ed-fi.org/5.2.0" targetNamespace="http://ed-fi.org/5.2.0" elementFormDefault="qualified" attributeFormDefault="unqualified">
                      <xs:include schemaLocation="EXTENSION-Ed-Fi-Extended-Core.xsd" />
                      <xs:annotation>
                        <xs:documentation>===== Survey Interchange Model =====</xs:documentation>
                      </xs:annotation>
                      <xs:element name="InterchangeSurvey">
                        <xs:annotation>
                          <xs:documentation>The Survey interchange describes survey metadata, including the definitions of the survey, survey sections, and survey questions making up the survey and survey responses from both identified and anonymous respondents.</xs:documentation>
                        </xs:annotation>
                        <xs:complexType>
                          <xs:choice maxOccurs="unbounded">
                            <xs:element name="Survey" type="Survey" />
                            <xs:element name="SurveyQuestion" type="SurveyQuestion" />
                            <xs:element name="SurveyQuestionResponse" type="SurveyQuestionResponse" />
                            <xs:element name="SurveyResponse" type="EXTENSION-SurveyResponseExtension" />
                            <xs:element name="SurveySection" type="SurveySection" />
                            <xs:element name="SurveySectionResponse" type="SurveySectionResponse" />
                            <xs:element name="SurveyCourseAssociation" type="SurveyCourseAssociation" />
                            <xs:element name="SurveySectionAssociation" type="SurveySectionAssociation" />
                            <xs:element name="SurveyProgramAssociation" type="SurveyProgramAssociation" />
                            <xs:element name="SurveyResponseEducationOrganizationTargetAssociation" type="SurveyResponseEducationOrganizationTargetAssociation" />
                            <xs:element name="SurveyResponseStaffTargetAssociation" type="SurveyResponseStaffTargetAssociation" />
                            <xs:element name="SurveySectionResponseEducationOrganizationTargetAssociation" type="SurveySectionResponseEducationOrganizationTargetAssociation" />
                            <xs:element name="SurveySectionResponseStaffTargetAssociation" type="SurveySectionResponseStaffTargetAssociation" />
                            <xs:element name="SurveyResponsePersonTargetAssociation" type="EXTENSION-SurveyResponsePersonTargetAssociation" />
                            <xs:element name="SurveySectionResponsePersonTargetAssociation" type="EXTENSION-SurveySectionResponsePersonTargetAssociation" />
                          </xs:choice>
                        </xs:complexType>
                      </xs:element>
                    </xs:schema>
                  """
        Scenario: 07 Ensure clients can retrieve XSD content of Sample Extension
             When a GET request is made to "/metadata/xsd/sample/EdFi.Sample.ApiSchema.xsd.Sample-EXTENSION-Interchange-StudentProgram-Extension.xsd"
             Then it should respond with 200
              And the xsd response body is
                  """
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns="http://ed-fi.org/5.2.0" targetNamespace="http://ed-fi.org/5.2.0" elementFormDefault="qualified" attributeFormDefault="unqualified">
                    <xs:include schemaLocation="EXTENSION-Ed-Fi-Extended-Core.xsd"/>
                    <xs:annotation>
                    <xs:documentation>===== Student Program Interchange Model =====</xs:documentation>
                    </xs:annotation>
                    <xs:element name="InterchangeStudentProgram">
                    <xs:annotation>
                    <xs:documentation>This interchange loads students' participation in programs.</xs:documentation>
                    </xs:annotation>
                    <xs:complexType>
                    <xs:choice maxOccurs="unbounded">
                    <xs:element name="StudentProgramAssociation" type="StudentProgramAssociation"/>
                    <xs:element name="StudentSpecialEducationProgramAssociation" type="StudentSpecialEducationProgramAssociation"/>
                    <xs:element name="RestraintEvent" type="RestraintEvent"/>
                    <xs:element name="StudentCTEProgramAssociation" type="EXTENSION-StudentCTEProgramAssociationExtension"/>
                    <xs:element name="StudentTitleIPartAProgramAssociation" type="StudentTitleIPartAProgramAssociation"/>
                    <xs:element name="StudentMigrantEducationProgramAssociation" type="StudentMigrantEducationProgramAssociation"/>
                    <xs:element name="StudentLanguageInstructionProgramAssociation" type="StudentLanguageInstructionProgramAssociation"/>
                    <xs:element name="StudentHomelessProgramAssociation" type="StudentHomelessProgramAssociation"/>
                    <xs:element name="StudentNeglectedOrDelinquentProgramAssociation" type="StudentNeglectedOrDelinquentProgramAssociation"/>
                    <xs:element name="StudentSchoolFoodServiceProgramAssociation" type="StudentSchoolFoodServiceProgramAssociation"/>
                    <xs:element name="StudentSection504ProgramAssociation" type="StudentSection504ProgramAssociation"/>
                    </xs:choice>
                    </xs:complexType>
                    </xs:element>
                    </xs:schema>
                  """
        Scenario: 08 Ensure clients can retrieve XSD content of Core
             When a GET request is made to "/metadata/xsd/ed-fi/EdFi.DataStandard52.ApiSchema.xsd.Interchange-Survey.xsd"
             Then it should respond with 200
              And the xsd response body is
                  """
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns="http://ed-fi.org/5.2.0" targetNamespace="http://ed-fi.org/5.2.0" elementFormDefault="qualified" attributeFormDefault="unqualified">
                    <xs:include schemaLocation="Ed-Fi-Core.xsd"/>
                    <xs:annotation>
                    <xs:documentation>===== Survey Interchange Model =====</xs:documentation>
                    </xs:annotation>
                    <xs:element name="InterchangeSurvey">
                    <xs:annotation>
                    <xs:documentation>The Survey interchange describes survey metadata, including the definitions of the survey, survey sections, and survey questions making up the survey and survey responses from both identified and anonymous respondents.</xs:documentation>
                    </xs:annotation>
                    <xs:complexType>
                    <xs:choice maxOccurs="unbounded">
                    <xs:element name="Survey" type="Survey"/>
                    <xs:element name="SurveyQuestion" type="SurveyQuestion"/>
                    <xs:element name="SurveyQuestionResponse" type="SurveyQuestionResponse"/>
                    <xs:element name="SurveyResponse" type="SurveyResponse"/>
                    <xs:element name="SurveySection" type="SurveySection"/>
                    <xs:element name="SurveySectionResponse" type="SurveySectionResponse"/>
                    <xs:element name="SurveyCourseAssociation" type="SurveyCourseAssociation"/>
                    <xs:element name="SurveySectionAssociation" type="SurveySectionAssociation"/>
                    <xs:element name="SurveyProgramAssociation" type="SurveyProgramAssociation"/>
                    <xs:element name="SurveyResponseEducationOrganizationTargetAssociation" type="SurveyResponseEducationOrganizationTargetAssociation"/>
                    <xs:element name="SurveyResponseStaffTargetAssociation" type="SurveyResponseStaffTargetAssociation"/>
                    <xs:element name="SurveySectionResponseEducationOrganizationTargetAssociation" type="SurveySectionResponseEducationOrganizationTargetAssociation"/>
                    <xs:element name="SurveySectionResponseStaffTargetAssociation" type="SurveySectionResponseStaffTargetAssociation"/>
                    </xs:choice>
                    </xs:complexType>
                    </xs:element>
                    </xs:schema>
                  """
