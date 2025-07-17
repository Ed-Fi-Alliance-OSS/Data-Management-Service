import { faker } from '../utils/faker-k6.js';

// Common descriptor values based on Ed-Fi standards
export const descriptorValues = {
    gradeLevel: [
        'First grade',
        'Second grade',
        'Third grade',
        'Fourth grade',
        'Fifth grade',
        'Sixth grade',
        'Seventh grade',
        'Eighth grade',
        'Ninth grade',
        'Tenth grade',
        'Eleventh grade',
        'Twelfth grade',
        'Kindergarten',
        'Preschool/Prekindergarten'
    ],
    
    academicSubject: [
        'English Language Arts',
        'Mathematics',
        'Science',
        'Social Studies',
        'Foreign Language and Literature',
        'Fine and Performing Arts',
        'Physical, Health, and Safety Education',
        'Career and Technical Education',
        'Computer Science'
    ],
    
    assessmentCategory: [
        'State assessment',
        'Benchmark test',
        'Formative assessment',
        'Performance assessment',
        'Achievement test',
        'Aptitude test',
        'Diagnostic',
        'Language proficiency test'
    ],
    
    attendanceEvent: [
        'In Attendance',
        'Excused Absence',
        'Unexcused Absence',
        'Tardy',
        'Early departure',
        'Partial'
    ],
    
    behaviorDescriptor: [
        'School Violation',
        'State Offense',
        'School Code of Conduct'
    ],
    
    calendarType: [
        'Student Specific',
        'School Calendar',
        'IEP',
        'Supplemental'
    ],
    
    courseLevel: [
        'Basic',
        'General',
        'Honors',
        'Advanced Placement',
        'International Baccalaureate',
        'Dual Credit',
        'Remedial'
    ],
    
    sex: [
        'Male',
        'Female'
    ],
    
    race: [
        'American Indian - Alaska Native',
        'Asian',
        'Black - African American',
        'Native Hawaiian - Pacific Islander',
        'White',
        'Choose Not to Respond'
    ],
    
    relationDescriptor: [
        'Mother',
        'Father',
        'Stepmother',
        'Stepfather',
        'Guardian',
        'Grandmother',
        'Grandfather',
        'Aunt',
        'Uncle',
        'Foster parent',
        'Other'
    ],
    
    staffClassification: [
        'Teacher',
        'Principal',
        'Assistant Principal',
        'Counselor',
        'Librarian',
        'Support Staff',
        'Instructional Aide',
        'Substitute Teacher',
        'Technology Coordinator',
        'School Nurse'
    ],
    
    telephoneNumber: [
        'Home',
        'Work',
        'Mobile',
        'Emergency',
        'Other'
    ],
    
    addressType: [
        'Home',
        'Physical',
        'Mailing',
        'Work',
        'Temporary',
        'Billing'
    ],
    
    electronicMail: [
        'Home/Personal',
        'Work',
        'Organization',
        'Other'
    ],
    
    schoolType: [
        'Regular',
        'Alternative',
        'Magnet',
        'Special Education',
        'Career and Technical Education',
        'Charter'
    ],
    
    term: [
        'Fall Semester',
        'Spring Semester',
        'Summer Semester',
        'First Quarter',
        'Second Quarter',
        'Third Quarter',
        'Fourth Quarter',
        'Year Round'
    ]
};

export function generateDescriptor(descriptorType, namespace = 'uri://ed-fi.org') {
    const values = descriptorValues[descriptorType] || ['Default Value'];
    const value = faker.helpers.arrayElement(values);
    
    return {
        codeValue: value.replace(/\s+/g, ''),
        shortDescription: value,
        description: value,
        namespace: `${namespace}/${descriptorType}Descriptor`
    };
}

export function generateAllDescriptors() {
    const descriptors = {};
    
    for (const [type, values] of Object.entries(descriptorValues)) {
        descriptors[`${type}Descriptors`] = values.map(value => ({
            codeValue: value.replace(/\s+/g, ''),
            shortDescription: value,
            description: value,
            namespace: `uri://ed-fi.org/${type}Descriptor`
        }));
    }
    
    return descriptors;
}

export function getRandomDescriptorUri(descriptorType) {
    const values = descriptorValues[descriptorType];
    if (!values) {
        throw new Error(`Unknown descriptor type: ${descriptorType}`);
    }
    
    const value = faker.helpers.arrayElement(values);
    return `uri://ed-fi.org/${descriptorType}Descriptor#${value.replace(/\s+/g, '')}`;
}