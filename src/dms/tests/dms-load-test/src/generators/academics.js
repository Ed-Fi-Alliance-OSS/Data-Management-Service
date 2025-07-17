import { faker } from '../utils/faker-k6.js';
import { getRandomDescriptorUri } from './descriptors.js';

export function generateCourse(educationOrganizationId, courseCode, courseTitle, academicSubject) {
    return {
        courseCode: courseCode,
        educationOrganizationReference: {
            educationOrganizationId: educationOrganizationId
        },
        courseTitle: courseTitle,
        numberOfParts: 1,
        academicSubjectDescriptor: `uri://ed-fi.org/AcademicSubjectDescriptor#${academicSubject.replace(/\s+/g, '')}`,
        courseDescription: faker.lorem.sentence(),
        dateCourseAdopted: faker.date.past({ years: 5 }).toISOString().split('T')[0],
        highSchoolCourseRequirement: faker.datatype.boolean({ probability: 0.3 }),
        courseGPAApplicabilityDescriptor: 'uri://ed-fi.org/CourseGPAApplicabilityDescriptor#Applicable',
        courseLevelCharacteristics: [
            {
                courseLevelCharacteristicDescriptor: getRandomDescriptorUri('courseLevel')
            }
        ],
        offeredGradeLevels: generateOfferedGradeLevels(academicSubject),
        identificationCodes: [
            {
                identificationCode: courseCode,
                courseIdentificationSystemDescriptor: 'uri://ed-fi.org/CourseIdentificationSystemDescriptor#District'
            }
        ],
        maximumAvailableCredits: faker.helpers.arrayElement([0.5, 1.0, 1.5, 2.0]),
        minimumAvailableCredits: faker.helpers.arrayElement([0.5, 1.0])
    };
}

export function generateCourseOffering(localCourseCode, schoolId, schoolYear, sessionName, courseCode) {
    return {
        localCourseCode: localCourseCode,
        schoolReference: {
            schoolId: schoolId
        },
        sessionReference: {
            schoolId: schoolId,
            schoolYear: schoolYear,
            sessionName: sessionName
        },
        courseReference: {
            courseCode: courseCode,
            educationOrganizationId: schoolId
        },
        localCourseTitle: faker.helpers.maybe(() => faker.company.catchPhrase(), { probability: 0.2 }),
        instructionalTimePlanned: faker.number.int({ min: 120, max: 180 }),
        courseOfferingStatusDescriptor: 'uri://ed-fi.org/CourseOfferingStatusDescriptor#Active'
    };
}

export function generateSection(sectionIdentifier, localCourseCode, schoolId, schoolYear, sessionName, classPeriodName) {
    return {
        sectionIdentifier: sectionIdentifier,
        courseOfferingReference: {
            localCourseCode: localCourseCode,
            schoolId: schoolId,
            schoolYear: schoolYear,
            sessionName: sessionName
        },
        classPeriods: [
            {
                classPeriodReference: {
                    classPeriodName: classPeriodName,
                    schoolId: schoolId
                }
            }
        ],
        classroomIdentificationCode: faker.helpers.arrayElement(['101', '102', '103', '201', '202', '203', 'GYM', 'LAB1', 'LAB2']),
        maximumAvailableCredits: faker.helpers.arrayElement([0.5, 1.0]),
        sequenceOfCourse: 1,
        availableCredits: faker.helpers.arrayElement([0.5, 1.0]),
        educationalEnvironmentDescriptor: 'uri://ed-fi.org/EducationalEnvironmentDescriptor#Classroom',
        instructionLanguageDescriptor: 'uri://ed-fi.org/LanguageDescriptor#English',
        locationSchoolId: schoolId,
        mediumOfInstructionDescriptor: 'uri://ed-fi.org/MediumOfInstructionDescriptor#Face-to-face',
        populationServedDescriptor: 'uri://ed-fi.org/PopulationServedDescriptor#Regular',
        availableCreditTypeDescriptor: 'uri://ed-fi.org/CreditTypeDescriptor#Regular',
        characteristics: []
    };
}

export function generateStudentSectionAssociation(studentUniqueId, sectionIdentifier, localCourseCode, schoolId, schoolYear, sessionName, beginDate) {
    return {
        sectionReference: {
            localCourseCode: localCourseCode,
            schoolId: schoolId,
            schoolYear: schoolYear,
            sectionIdentifier: sectionIdentifier,
            sessionName: sessionName
        },
        studentReference: {
            studentUniqueId: studentUniqueId
        },
        beginDate: beginDate,
        endDate: null,
        homeroomIndicator: faker.datatype.boolean({ probability: 0.1 }),
        teacherStudentDataLinkExclusion: false,
        attemptStatusDescriptor: 'uri://ed-fi.org/AttemptStatusDescriptor#InProgress',
        repeatIdentifierDescriptor: 'uri://ed-fi.org/RepeatIdentifierDescriptor#NotRepeated'
    };
}

export function generateGrade(studentUniqueId, sectionIdentifier, localCourseCode, schoolId, schoolYear, sessionName, gradingPeriodDescriptor, gradingPeriodSequence, gradingPeriodSchoolYear, beginDate) {
    const letterGrades = ['A+', 'A', 'A-', 'B+', 'B', 'B-', 'C+', 'C', 'C-', 'D+', 'D', 'F'];
    const numericGrades = [100, 98, 95, 92, 90, 88, 85, 82, 80, 78, 75, 72, 70, 68, 65, 60, 55, 50];
    
    return {
        gradingPeriodReference: {
            gradingPeriodDescriptor: gradingPeriodDescriptor,
            periodSequence: gradingPeriodSequence,
            schoolId: schoolId,
            schoolYear: gradingPeriodSchoolYear
        },
        studentSectionAssociationReference: {
            beginDate: beginDate,
            localCourseCode: localCourseCode,
            schoolId: schoolId,
            schoolYear: schoolYear,
            sectionIdentifier: sectionIdentifier,
            sessionName: sessionName,
            studentUniqueId: studentUniqueId
        },
        gradeTypeDescriptor: 'uri://ed-fi.org/GradeTypeDescriptor#Grading Period',
        letterGradeEarned: faker.helpers.arrayElement(letterGrades),
        numericGradeEarned: faker.helpers.arrayElement(numericGrades),
        diagnosticStatement: faker.helpers.maybe(() => faker.lorem.sentence(), { probability: 0.1 }),
        performanceBaseConversionDescriptor: null
    };
}

export function generateAssessment(assessmentIdentifier, assessmentTitle, namespace, academicSubjects) {
    return {
        assessmentIdentifier: assessmentIdentifier,
        namespace: namespace,
        assessmentTitle: assessmentTitle,
        assessmentCategoryDescriptor: getRandomDescriptorUri('assessmentCategory'),
        assessmentForm: faker.helpers.arrayElement(['Online', 'Paper', 'Mixed']),
        assessmentVersion: faker.number.int({ min: 1, max: 5 }),
        revisionDate: faker.date.past({ years: 2 }).toISOString().split('T')[0],
        maxRawScore: faker.helpers.arrayElement([100, 200, 800, 1600]),
        nomenclature: 'Standard',
        assessmentFamily: assessmentTitle.split(' ')[0],
        educationOrganizationReference: {
            educationOrganizationId: 100000 // District level
        },
        adaptiveAssessment: faker.datatype.boolean({ probability: 0.2 }),
        academicSubjects: academicSubjects.map(subject => ({
            academicSubjectDescriptor: `uri://ed-fi.org/AcademicSubjectDescriptor#${subject.replace(/\s+/g, '')}`
        })),
        assessedGradeLevels: generateAssessedGradeLevels(),
        scores: generateAssessmentScores()
    };
}

export function generateStudentAssessment(studentUniqueId, assessmentIdentifier, namespace, administrationDate) {
    return {
        assessmentReference: {
            assessmentIdentifier: assessmentIdentifier,
            namespace: namespace
        },
        studentReference: {
            studentUniqueId: studentUniqueId
        },
        administrationDate: administrationDate,
        administrationEndDate: administrationDate,
        serialNumber: faker.string.alphanumeric({ length: 10, casing: 'upper' }),
        administrationLanguageDescriptor: 'uri://ed-fi.org/LanguageDescriptor#English',
        administrationEnvironmentDescriptor: 'uri://ed-fi.org/AdministrationEnvironmentDescriptor#School',
        retestIndicatorDescriptor: 'uri://ed-fi.org/RetestIndicatorDescriptor#Primary',
        reasonNotTestedDescriptor: null,
        whenAssessedGradeLevelDescriptor: getRandomDescriptorUri('gradeLevel'),
        eventCircumstanceDescriptor: null,
        eventDescription: null,
        schoolYear: 2024,
        scoreResults: generateStudentScoreResults(),
        performanceLevels: generateStudentPerformanceLevels()
    };
}

function generateOfferedGradeLevels(academicSubject) {
    const elementaryGrades = ['Kindergarten', 'First grade', 'Second grade', 'Third grade', 'Fourth grade', 'Fifth grade'];
    const middleGrades = ['Sixth grade', 'Seventh grade', 'Eighth grade'];
    const highGrades = ['Ninth grade', 'Tenth grade', 'Eleventh grade', 'Twelfth grade'];
    
    let grades;
    if (['Mathematics', 'English Language Arts', 'Science', 'Social Studies'].includes(academicSubject)) {
        grades = faker.helpers.arrayElement([elementaryGrades, middleGrades, highGrades]);
    } else {
        grades = faker.helpers.arrayElement([middleGrades, highGrades]);
    }
    
    return grades.map(grade => ({
        gradeLevelDescriptor: `uri://ed-fi.org/GradeLevelDescriptor#${grade.replace(/\s+/g, '')}`
    }));
}

function generateAssessedGradeLevels() {
    const gradeGroups = [
        ['Third grade', 'Fourth grade', 'Fifth grade'],
        ['Sixth grade', 'Seventh grade', 'Eighth grade'],
        ['Ninth grade', 'Tenth grade', 'Eleventh grade']
    ];
    
    const selectedGroup = faker.helpers.arrayElement(gradeGroups);
    
    return selectedGroup.map(grade => ({
        gradeLevelDescriptor: `uri://ed-fi.org/GradeLevelDescriptor#${grade.replace(/\s+/g, '')}`
    }));
}

function generateAssessmentScores() {
    return [
        {
            assessmentReportingMethodDescriptor: 'uri://ed-fi.org/AssessmentReportingMethodDescriptor#RawScore',
            maximumScore: '100',
            minimumScore: '0',
            resultDatatypeTypeDescriptor: 'uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer'
        },
        {
            assessmentReportingMethodDescriptor: 'uri://ed-fi.org/AssessmentReportingMethodDescriptor#ScaleScore',
            maximumScore: '800',
            minimumScore: '200',
            resultDatatypeTypeDescriptor: 'uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer'
        }
    ];
}

function generateStudentScoreResults() {
    return [
        {
            assessmentReportingMethodDescriptor: 'uri://ed-fi.org/AssessmentReportingMethodDescriptor#RawScore',
            resultDatatypeTypeDescriptor: 'uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer',
            result: faker.number.int({ min: 60, max: 100 }).toString()
        },
        {
            assessmentReportingMethodDescriptor: 'uri://ed-fi.org/AssessmentReportingMethodDescriptor#ScaleScore',
            resultDatatypeTypeDescriptor: 'uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer',
            result: faker.number.int({ min: 400, max: 800 }).toString()
        }
    ];
}

function generateStudentPerformanceLevels() {
    const performanceLevels = ['Advanced', 'Proficient', 'Basic', 'Below Basic'];
    
    return [
        {
            assessmentReportingMethodDescriptor: 'uri://ed-fi.org/AssessmentReportingMethodDescriptor#ProficiencyLevel',
            performanceLevelDescriptor: `uri://ed-fi.org/PerformanceLevelDescriptor#${faker.helpers.arrayElement(performanceLevels)}`,
            performanceLevelMet: faker.datatype.boolean({ probability: 0.7 })
        }
    ];
}