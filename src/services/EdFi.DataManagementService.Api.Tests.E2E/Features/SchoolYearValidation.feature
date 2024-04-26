@SchoolYear
Feature: School Year Reference Validation
  Reference validation on School Years

  """
  @StudentEducationOrganizationAssociation Hook that will create the following data for the required tests
  createStudent(studentUniqueId)
  createSchool(schoolId)
  createCourse(courseCode, schoolId)
  createSession(schoolId, schoolYear, sessionName)
  createCourseOffering(localCourseCode, courseCode, schoolId, schoolYear, sessionName)
  createSection(localCourseCode, schoolId, schoolYear, sessionName, sectionIdentifier)
  """

  @Ignore @StudentEducationOrganizationAssociation
  Scenario: Try creating a resource using a valid school year
    When sending a POST request to "/ed-fi/studentSectionAssociation" with body
    """
        {
        "beginDate": "2024-04-25",
        "sectionReference": {
            "localCourseCode": "string",
            "schoolId": 0,
            "schoolYear": 0,
            "sectionIdentifier": "string",
            "sessionName": "string",
        },
        "studentReference": {
            "studentUniqueId": "string",
        }
        }
    """
    Then the response code is 201
    And the response body is
    """
    """

  @Ignore @StudentEducationOrganizationAssociation
  Scenario: Try creating a resource using an invalid school year
    When sending a POST request to "/ed-fi/studentSectionAssociation" with body
    """
        {
        "beginDate": "2024-04-25",
        "sectionReference": {
            "localCourseCode": "string",
            "schoolId": 0,
            "schoolYear": 0,
            "sectionIdentifier": "string",
            "sessionName": "string",
        },
        "studentReference": {
            "studentUniqueId": "string",
        }
        }
    """
    Then the response code is 400

  #Course Offering / School Year
  @Ignore
  Scenario: Post a valid request using an existing CourseOffering
    Given a existing CourseOffering
    When sending a POST request to "/ed-fi/courseOfferings" with body
    """
        {
        "localCourseCode": "string",
        "courseReference": {
            "courseCode": "string",
            "educationOrganizationId": 0
        },
        "schoolReference": {
            "schoolId": 0
        },
        "sessionReference": {
            "schoolId": 0,
            "schoolYear": 0,
            "sessionName": "string"
        }
        }
    """
    Then the response code is 201
    And the response body is
    """
    """

  @Ignore
  Scenario: Post a valid request using a non existing CourseOffering and API will handle this correctly
    When sending a POST request to "/ed-fi/courseOfferings" with body
    """
        {
        "localCourseCode": "string",
        "courseReference": {
            "courseCode": "string",
            "educationOrganizationId": 0
        },
        "schoolReference": {
            "schoolId": 0
        },
        "sessionReference": {
            "schoolId": 0,
            "schoolYear": 0,
            "sessionName": "string"
        }
        }
    """
    Then the response code is 400


  """
  The cohort year scenario is interesting because it is testing a situation where a resource has a collection of SchoolYear references.
  The StudentEducationOrganizationAssociation has this collection, via CohortYears.
  """
  @Ignore
  Scenario: Handling the array with two valid cohorts
    When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
    """
    {
        "educationOrganizationReference": {
            "educationOrganizationId": 0
        },
        "studentReference": {
            "studentUniqueId": "string"
        },
        "cohortYears": [
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            },
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            }
        ]
    }
    """
    Then the response code is 200

  @Ignore
  Scenario: Handling the array with 2 cohorts (1st valid / 2nd invalid)
    When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
    """
    {
        "educationOrganizationReference": {
            "educationOrganizationId": 0
        },
        "studentReference": {
            "studentUniqueId": "string"
        },
        "cohortYears": [
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            },
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            }
        ]
    }
    """
    Then the response code is 400

  @Ignore
  Scenario: Handling the array with 2 cohorts (1st invalid / 2nd valid)
    When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
    """
    {
        "educationOrganizationReference": {
            "educationOrganizationId": 0
        },
        "studentReference": {
            "studentUniqueId": "string"
        },
        "cohortYears": [
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            },
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            }
        ]
    }
    """
    Then the response code is 400
