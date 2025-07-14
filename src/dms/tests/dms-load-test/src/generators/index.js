import { faker } from '../utils/faker-k6.js';
import { generateDescriptor, getRandomDescriptorUri } from './descriptors.js';
import { generateLocalEducationAgency, generateSchool, generateCalendar, generateCalendarDate, generateClassPeriod } from './schools.js';
import { generateStudent, generateStudentEducationOrganizationAssociation, generateStudentSchoolAssociation, generateParent, generateStudentParentAssociation } from './students.js';
import { generateStaff, generateStaffEducationOrganizationAssignmentAssociation, generateStaffSchoolAssociation, generateStaffSectionAssociation } from './staff.js';
import { generateCourse, generateCourseOffering, generateSection, generateStudentSectionAssociation, generateGrade, generateAssessment, generateStudentAssessment } from './academics.js';

export class DataGenerator {
    constructor(config = {}) {
        this.config = {
            schoolCount: parseInt(__ENV.SCHOOL_COUNT) || config.schoolCount || 130,
            studentCount: parseInt(__ENV.STUDENT_COUNT) || config.studentCount || 75000,
            staffCount: parseInt(__ENV.STAFF_COUNT) || config.staffCount || 12000,
            coursesPerSchool: parseInt(__ENV.COURSES_PER_SCHOOL) || config.coursesPerSchool || 50,
            sectionsPerCourse: parseInt(__ENV.SECTIONS_PER_COURSE) || config.sectionsPerCourse || 4,
            schoolYear: config.schoolYear || 2024,
            ...config
        };
        
        // Set seed for reproducible data
        faker.seed(this.config.seed || 12345);
    }

    generateForResourceType(resourceType, count = 1, dependencies = {}) {
        const generators = {
            // Descriptors
            'gradeLevelDescriptors': () => generateDescriptor('gradeLevel'),
            'academicSubjectDescriptors': () => generateDescriptor('academicSubject'),
            'assessmentCategoryDescriptors': () => generateDescriptor('assessmentCategory'),
            'attendanceEventCategoryDescriptors': () => generateDescriptor('attendanceEvent'),
            'behaviorDescriptors': () => generateDescriptor('behaviorDescriptor'),
            'calendarTypeDescriptors': () => generateDescriptor('calendarType'),
            'courseLevelCharacteristicDescriptors': () => generateDescriptor('courseLevel'),
            
            // Organizations
            'localEducationAgencies': () => generateLocalEducationAgency(),
            'schools': () => {
                const leaId = dependencies.localEducationAgencyId || 100000;
                const schoolId = faker.number.int({ min: 1000, max: 9999 });
                return generateSchool(leaId, schoolId);
            },
            
            // Calendar
            'calendars': () => {
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                return generateCalendar(schoolId, this.config.schoolYear);
            },
            'calendarDates': () => {
                const calendarCode = dependencies.calendarCode || `CAL-${faker.number.int({ min: 1000, max: 9999 })}`;
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const date = faker.date.between({ 
                    from: `${this.config.schoolYear}-08-01`, 
                    to: `${this.config.schoolYear + 1}-06-30` 
                }).toISOString().split('T')[0];
                return generateCalendarDate(calendarCode, schoolId, this.config.schoolYear, date);
            },
            'classPeriods': () => {
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const periodName = `Period ${faker.number.int({ min: 1, max: 8 })}`;
                return generateClassPeriod(schoolId, periodName);
            },
            
            // People
            'students': () => generateStudent(),
            'parents': () => generateParent(),
            'staffs': () => generateStaff(),
            
            // Associations
            'studentEducationOrganizationAssociations': () => {
                const studentUniqueId = dependencies.studentUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const educationOrganizationId = dependencies.educationOrganizationId || 100000;
                return generateStudentEducationOrganizationAssociation(studentUniqueId, educationOrganizationId);
            },
            'studentSchoolAssociations': () => {
                const studentUniqueId = dependencies.studentUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const entryDate = dependencies.entryDate || `${this.config.schoolYear}-08-15`;
                const gradeLevel = dependencies.gradeLevel || faker.helpers.arrayElement(['Ninth grade', 'Tenth grade']);
                return generateStudentSchoolAssociation(studentUniqueId, schoolId, entryDate, gradeLevel);
            },
            'studentParentAssociations': () => {
                const studentUniqueId = dependencies.studentUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const parentUniqueId = dependencies.parentUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                return generateStudentParentAssociation(studentUniqueId, parentUniqueId);
            },
            'staffEducationOrganizationAssignmentAssociations': () => {
                const staffUniqueId = dependencies.staffUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const educationOrganizationId = dependencies.educationOrganizationId || 100000;
                const beginDate = dependencies.beginDate || `${this.config.schoolYear}-08-01`;
                return generateStaffEducationOrganizationAssignmentAssociation(staffUniqueId, educationOrganizationId, beginDate);
            },
            'staffSchoolAssociations': () => {
                const staffUniqueId = dependencies.staffUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const programName = dependencies.programName || 'Regular Education';
                const academicSubjects = dependencies.academicSubjects || ['Mathematics'];
                return generateStaffSchoolAssociation(staffUniqueId, schoolId, programName, academicSubjects);
            },
            
            // Academic
            'courses': () => {
                const educationOrganizationId = dependencies.educationOrganizationId || 100000;
                const courseCode = dependencies.courseCode || `COURSE-${faker.number.int({ min: 100, max: 999 })}`;
                const courseTitle = dependencies.courseTitle || faker.company.catchPhrase();
                const academicSubject = dependencies.academicSubject || 'Mathematics';
                return generateCourse(educationOrganizationId, courseCode, courseTitle, academicSubject);
            },
            'courseOfferings': () => {
                const localCourseCode = dependencies.localCourseCode || `LOCAL-${faker.number.int({ min: 100, max: 999 })}`;
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const sessionName = dependencies.sessionName || 'Fall Semester';
                const courseCode = dependencies.courseCode || `COURSE-${faker.number.int({ min: 100, max: 999 })}`;
                return generateCourseOffering(localCourseCode, schoolId, this.config.schoolYear, sessionName, courseCode);
            },
            'sections': () => {
                const sectionIdentifier = dependencies.sectionIdentifier || `SEC-${faker.number.int({ min: 1000, max: 9999 })}`;
                const localCourseCode = dependencies.localCourseCode || `LOCAL-${faker.number.int({ min: 100, max: 999 })}`;
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const sessionName = dependencies.sessionName || 'Fall Semester';
                const classPeriodName = dependencies.classPeriodName || 'Period 1';
                return generateSection(sectionIdentifier, localCourseCode, schoolId, this.config.schoolYear, sessionName, classPeriodName);
            },
            'studentSectionAssociations': () => {
                const studentUniqueId = dependencies.studentUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const sectionIdentifier = dependencies.sectionIdentifier || `SEC-${faker.number.int({ min: 1000, max: 9999 })}`;
                const localCourseCode = dependencies.localCourseCode || `LOCAL-${faker.number.int({ min: 100, max: 999 })}`;
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const sessionName = dependencies.sessionName || 'Fall Semester';
                const beginDate = dependencies.beginDate || `${this.config.schoolYear}-08-15`;
                return generateStudentSectionAssociation(studentUniqueId, sectionIdentifier, localCourseCode, schoolId, this.config.schoolYear, sessionName, beginDate);
            },
            'grades': () => {
                const studentUniqueId = dependencies.studentUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const sectionIdentifier = dependencies.sectionIdentifier || `SEC-${faker.number.int({ min: 1000, max: 9999 })}`;
                const localCourseCode = dependencies.localCourseCode || `LOCAL-${faker.number.int({ min: 100, max: 999 })}`;
                const schoolId = dependencies.schoolId || faker.number.int({ min: 1000, max: 9999 });
                const sessionName = dependencies.sessionName || 'Fall Semester';
                const gradingPeriodDescriptor = dependencies.gradingPeriodDescriptor || 'uri://ed-fi.org/GradingPeriodDescriptor#FirstSixWeeks';
                const gradingPeriodSequence = dependencies.gradingPeriodSequence || 1;
                const beginDate = dependencies.beginDate || `${this.config.schoolYear}-08-15`;
                return generateGrade(studentUniqueId, sectionIdentifier, localCourseCode, schoolId, this.config.schoolYear, sessionName, gradingPeriodDescriptor, gradingPeriodSequence, this.config.schoolYear, beginDate);
            },
            'assessments': () => {
                const assessmentIdentifier = dependencies.assessmentIdentifier || `ASSESS-${faker.number.int({ min: 1000, max: 9999 })}`;
                const assessmentTitle = dependencies.assessmentTitle || faker.company.catchPhrase();
                const namespace = dependencies.namespace || 'uri://ed-fi.org/Assessment';
                const academicSubjects = dependencies.academicSubjects || ['Mathematics'];
                return generateAssessment(assessmentIdentifier, assessmentTitle, namespace, academicSubjects);
            },
            'studentAssessments': () => {
                const studentUniqueId = dependencies.studentUniqueId || faker.string.alphanumeric({ length: 10, casing: 'upper' });
                const assessmentIdentifier = dependencies.assessmentIdentifier || `ASSESS-${faker.number.int({ min: 1000, max: 9999 })}`;
                const namespace = dependencies.namespace || 'uri://ed-fi.org/Assessment';
                const administrationDate = dependencies.administrationDate || faker.date.between({ 
                    from: `${this.config.schoolYear}-09-01`, 
                    to: `${this.config.schoolYear + 1}-05-31` 
                }).toISOString().split('T')[0];
                return generateStudentAssessment(studentUniqueId, assessmentIdentifier, namespace, administrationDate);
            }
        };

        const generator = generators[resourceType];
        if (!generator) {
            throw new Error(`No generator found for resource type: ${resourceType}`);
        }

        const results = [];
        for (let i = 0; i < count; i++) {
            results.push(generator());
        }

        return count === 1 ? results[0] : results;
    }

    // Generate complete test data set for Austin ISD scale
    generateAustinISDData() {
        const data = {
            descriptors: {},
            organizations: {},
            people: {},
            academic: {},
            associations: {}
        };

        console.log('Generating Austin ISD scale data...');

        // Generate LEA
        data.organizations.localEducationAgency = generateLocalEducationAgency();
        
        // Generate schools
        data.organizations.schools = [];
        for (let i = 0; i < this.config.schoolCount; i++) {
            const school = generateSchool(data.organizations.localEducationAgency.localEducationAgencyId, 1000 + i);
            data.organizations.schools.push(school);
        }

        // Calculate distributions
        const studentsPerSchool = Math.floor(this.config.studentCount / this.config.schoolCount);
        const staffPerSchool = Math.floor(this.config.staffCount / this.config.schoolCount);

        console.log(`Generated ${this.config.schoolCount} schools`);
        console.log(`Distributing ${studentsPerSchool} students per school`);
        console.log(`Distributing ${staffPerSchool} staff per school`);

        return data;
    }
}