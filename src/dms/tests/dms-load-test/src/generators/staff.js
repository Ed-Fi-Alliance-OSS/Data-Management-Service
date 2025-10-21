import { faker } from '../utils/faker-k6.js';
import { getRandomDescriptorUri } from './descriptors.js';

export function generateStaff() {
    const firstName = faker.person.firstName();
    const lastName = faker.person.lastName();
    const middleName = faker.helpers.maybe(() => faker.person.middleName(), { probability: 0.7 });
    
    return {
        staffUniqueId: faker.string.alphanumeric({ length: 10, casing: 'upper' }),
        firstName: firstName,
        lastSurname: lastName,
        middleName: middleName,
        birthDate: faker.date.birthdate({ min: 22, max: 65, mode: 'age' }).toISOString().split('T')[0],
        generationCodeSuffix: faker.helpers.maybe(() => 
            faker.helpers.arrayElement(['Jr', 'Sr', 'III', 'IV']), 
            { probability: 0.05 }
        ),
        maidenName: faker.helpers.maybe(() => faker.person.lastName(), { probability: 0.2 }),
        personalTitlePrefix: faker.helpers.arrayElement(['Mr', 'Mrs', 'Ms', 'Dr']),
        sexDescriptor: getRandomDescriptorUri('sex'),
        hispanicLatinoEthnicity: faker.datatype.boolean({ probability: 0.25 }),
        oldEthnicityDescriptor: 'uri://ed-fi.org/OldEthnicityDescriptor#NotSpecified',
        highestCompletedLevelOfEducationDescriptor: faker.helpers.arrayElement([
            'uri://ed-fi.org/LevelOfEducationDescriptor#Bachelor\'s',
            'uri://ed-fi.org/LevelOfEducationDescriptor#Master\'s',
            'uri://ed-fi.org/LevelOfEducationDescriptor#Doctorate'
        ]),
        yearsOfPriorProfessionalExperience: faker.number.float({ min: 0, max: 30, precision: 0.1 }),
        yearsOfPriorTeachingExperience: faker.number.float({ min: 0, max: 25, precision: 0.1 }),
        highlyQualifiedTeacher: faker.datatype.boolean({ probability: 0.8 }),
        loginId: faker.internet.email({ firstName, lastName }).toLowerCase(),
        citizenshipStatusDescriptor: 'uri://ed-fi.org/CitizenshipStatusDescriptor#USCitizen',
        races: generateRaces(),
        addresses: [
            {
                addressTypeDescriptor: getRandomDescriptorUri('addressType'),
                city: faker.location.city(),
                postalCode: faker.location.zipCode(),
                stateAbbreviationDescriptor: 'uri://ed-fi.org/StateAbbreviationDescriptor#TX',
                streetNumberName: faker.location.streetAddress(),
                apartmentRoomSuiteNumber: faker.helpers.maybe(() => 
                    faker.location.secondaryAddress(), 
                    { probability: 0.2 }
                )
            }
        ],
        telephones: [
            {
                telephoneNumber: faker.phone.number('###-###-####'),
                telephoneNumberTypeDescriptor: getRandomDescriptorUri('telephoneNumber')
            }
        ],
        electronicMails: [
            {
                electronicMailAddress: faker.internet.email({ firstName, lastName }),
                electronicMailTypeDescriptor: getRandomDescriptorUri('electronicMail'),
                primaryEmailAddressIndicator: true
            }
        ],
        internationalAddresses: [],
        identificationCodes: [
            {
                assigningOrganizationIdentificationCode: 'District',
                identificationCode: faker.string.numeric({ length: 6 }),
                staffIdentificationSystemDescriptor: 'uri://ed-fi.org/StaffIdentificationSystemDescriptor#District'
            }
        ],
        credentials: generateCredentials()
    };
}

export function generateStaffEducationOrganizationAssignmentAssociation(staffUniqueId, educationOrganizationId, beginDate) {
    return {
        educationOrganizationReference: {
            educationOrganizationId: educationOrganizationId
        },
        staffReference: {
            staffUniqueId: staffUniqueId
        },
        staffClassificationDescriptor: getRandomDescriptorUri('staffClassification'),
        beginDate: beginDate,
        endDate: null,
        orderOfAssignment: faker.number.int({ min: 1, max: 3 }),
        employmentEducationOrganizationReference: {
            educationOrganizationId: educationOrganizationId
        },
        employmentStatusDescriptor: 'uri://ed-fi.org/EmploymentStatusDescriptor#Tenured',
        employmentHireDate: faker.date.past({ years: 10 }).toISOString().split('T')[0],
        positionTitle: faker.helpers.arrayElement([
            'Teacher',
            'Principal',
            'Assistant Principal',
            'Counselor',
            'Librarian',
            'Department Chair'
        ])
    };
}

export function generateStaffSchoolAssociation(staffUniqueId, schoolId, programName, academicSubjects) {
    return {
        staffReference: {
            staffUniqueId: staffUniqueId
        },
        schoolReference: {
            schoolId: schoolId
        },
        programAssignmentDescriptor: `uri://ed-fi.org/ProgramAssignmentDescriptor#${programName.replace(/\s+/g, '')}`,
        schoolYear: 2024,
        academicSubjects: academicSubjects.map(subject => ({
            academicSubjectDescriptor: `uri://ed-fi.org/AcademicSubjectDescriptor#${subject.replace(/\s+/g, '')}`
        })),
        gradeLevels: generateTeachingGradeLevels()
    };
}

export function generateStaffSectionAssociation(staffUniqueId, sectionIdentifier, courseCode, schoolId, schoolYear, termDescriptor) {
    return {
        sectionReference: {
            localCourseCode: courseCode,
            schoolId: schoolId,
            schoolYear: schoolYear,
            sectionIdentifier: sectionIdentifier,
            sessionName: `${schoolYear}-${termDescriptor.split('#')[1]}`
        },
        staffReference: {
            staffUniqueId: staffUniqueId
        },
        classroomPositionDescriptor: 'uri://ed-fi.org/ClassroomPositionDescriptor#TeacherOfRecord',
        beginDate: '2024-08-15',
        endDate: null,
        highlyQualifiedTeacher: faker.datatype.boolean({ probability: 0.8 }),
        teacherStudentDataLinkExclusion: false,
        percentageContribution: 100
    };
}

function generateRaces() {
    const races = [];
    const numberOfRaces = faker.helpers.arrayElement([1, 1, 1, 2]);
    
    for (let i = 0; i < numberOfRaces; i++) {
        races.push({
            raceDescriptor: getRandomDescriptorUri('race')
        });
    }
    
    return races;
}

function generateCredentials() {
    const credentials = [];
    const numberOfCredentials = faker.helpers.arrayElement([1, 1, 2]);
    
    for (let i = 0; i < numberOfCredentials; i++) {
        credentials.push({
            credentialIdentifier: faker.string.alphanumeric({ length: 12, casing: 'upper' }),
            stateOfIssueStateAbbreviationDescriptor: 'uri://ed-fi.org/StateAbbreviationDescriptor#TX',
            credentialFieldDescriptor: faker.helpers.arrayElement([
                'uri://ed-fi.org/CredentialFieldDescriptor#ElementaryEducation',
                'uri://ed-fi.org/CredentialFieldDescriptor#Mathematics',
                'uri://ed-fi.org/CredentialFieldDescriptor#Science',
                'uri://ed-fi.org/CredentialFieldDescriptor#EnglishLanguageArts',
                'uri://ed-fi.org/CredentialFieldDescriptor#SocialStudies'
            ]),
            credentialTypeDescriptor: 'uri://ed-fi.org/CredentialTypeDescriptor#Certification',
            levelDescriptor: 'uri://ed-fi.org/LevelDescriptor#AllLevels',
            teachingCredentialDescriptor: 'uri://ed-fi.org/TeachingCredentialDescriptor#Regular',
            issuanceDate: faker.date.past({ years: 5 }).toISOString().split('T')[0],
            expirationDate: faker.date.future({ years: 5 }).toISOString().split('T')[0]
        });
    }
    
    return credentials;
}

function generateTeachingGradeLevels() {
    const gradeLevelGroups = [
        ['Kindergarten', 'First grade', 'Second grade'],
        ['Third grade', 'Fourth grade', 'Fifth grade'],
        ['Sixth grade', 'Seventh grade', 'Eighth grade'],
        ['Ninth grade', 'Tenth grade', 'Eleventh grade', 'Twelfth grade']
    ];
    
    const selectedGroup = faker.helpers.arrayElement(gradeLevelGroups);
    
    return selectedGroup.map(grade => ({
        gradeLevelDescriptor: `uri://ed-fi.org/GradeLevelDescriptor#${grade.replace(/\s+/g, '')}`
    }));
}