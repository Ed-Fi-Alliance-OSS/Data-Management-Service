// k6-compatible faker replacement
// This provides basic data generation functions that work with k6's module system

// Seed for reproducible random values
let seed = 12345;

// Simple linear congruential generator for reproducible randomness
function seededRandom() {
    seed = (seed * 1664525 + 1013904223) % 4294967296;
    return seed / 4294967296;
}

// Set the seed
export function setSeed(newSeed) {
    seed = newSeed;
}

// Random integer between min and max (inclusive)
export function randomInt(min, max) {
    return Math.floor(seededRandom() * (max - min + 1)) + min;
}

// Random element from array
export function randomElement(array) {
    return array[Math.floor(seededRandom() * array.length)];
}

// Data arrays
const firstNames = [
    'James', 'Mary', 'John', 'Patricia', 'Robert', 'Jennifer', 'Michael', 'Linda',
    'William', 'Elizabeth', 'David', 'Barbara', 'Richard', 'Susan', 'Joseph', 'Jessica',
    'Thomas', 'Sarah', 'Charles', 'Karen', 'Christopher', 'Nancy', 'Daniel', 'Lisa',
    'Matthew', 'Betty', 'Anthony', 'Dorothy', 'Donald', 'Sandra', 'Mark', 'Ashley',
    'Paul', 'Kimberly', 'Steven', 'Donna', 'Andrew', 'Emily', 'Kenneth', 'Michelle',
    'Joshua', 'Carol', 'George', 'Amanda', 'Kevin', 'Melissa', 'Brian', 'Deborah',
    'Edward', 'Stephanie', 'Ronald', 'Rebecca', 'Timothy', 'Laura', 'Jason', 'Sharon',
    'Jeffrey', 'Cynthia', 'Ryan', 'Kathleen', 'Jacob', 'Helen', 'Gary', 'Amy',
    'Nicholas', 'Shirley', 'Eric', 'Angela', 'Stephen', 'Anna', 'Jonathan', 'Brenda',
    'Larry', 'Emma', 'Justin', 'Ruth', 'Scott', 'Virginia', 'Brandon', 'Katherine'
];

const lastNames = [
    'Smith', 'Johnson', 'Williams', 'Brown', 'Jones', 'Garcia', 'Miller', 'Davis',
    'Rodriguez', 'Martinez', 'Hernandez', 'Lopez', 'Gonzalez', 'Wilson', 'Anderson', 'Thomas',
    'Taylor', 'Moore', 'Jackson', 'Martin', 'Lee', 'Perez', 'Thompson', 'White',
    'Harris', 'Sanchez', 'Clark', 'Ramirez', 'Lewis', 'Robinson', 'Walker', 'Young',
    'Allen', 'King', 'Wright', 'Scott', 'Torres', 'Nguyen', 'Hill', 'Flores',
    'Green', 'Adams', 'Nelson', 'Baker', 'Hall', 'Rivera', 'Campbell', 'Mitchell',
    'Carter', 'Roberts', 'Gomez', 'Phillips', 'Evans', 'Turner', 'Diaz', 'Parker',
    'Cruz', 'Edwards', 'Collins', 'Reyes', 'Stewart', 'Morris', 'Morales', 'Murphy',
    'Cook', 'Rogers', 'Gutierrez', 'Ortiz', 'Morgan', 'Cooper', 'Peterson', 'Bailey',
    'Reed', 'Kelly', 'Howard', 'Ramos', 'Kim', 'Cox', 'Ward', 'Richardson'
];

const streetNames = [
    'Main', 'First', 'Second', 'Third', 'Fourth', 'Fifth', 'Park', 'Oak',
    'Pine', 'Maple', 'Cedar', 'Elm', 'Washington', 'Lake', 'Hill', 'Church',
    'Market', 'High', 'School', 'Spring', 'North', 'South', 'East', 'West',
    'Center', 'Union', 'Broadway', 'Riverside', 'Franklin', 'Jackson', 'Jefferson', 'Lincoln'
];

const streetSuffixes = ['St', 'Ave', 'Rd', 'Blvd', 'Dr', 'Ln', 'Way', 'Ct', 'Pl', 'Cir'];

const cities = [
    'Austin', 'Houston', 'Dallas', 'San Antonio', 'Fort Worth', 'El Paso', 'Arlington', 'Corpus Christi',
    'Plano', 'Laredo', 'Lubbock', 'Garland', 'Irving', 'Amarillo', 'Grand Prairie', 'Brownsville',
    'McKinney', 'Frisco', 'Pasadena', 'Mesquite', 'Killeen', 'McAllen', 'Waco', 'Carrollton',
    'Denton', 'Midland', 'Abilene', 'Beaumont', 'Round Rock', 'The Woodlands', 'Odessa', 'Richardson'
];

const schoolTypes = ['Elementary School', 'Middle School', 'High School', 'Academy', 'Preparatory School'];

const subjects = [
    'Mathematics', 'English Language Arts', 'Science', 'Social Studies', 'History',
    'Geography', 'Physical Education', 'Art', 'Music', 'Computer Science',
    'Spanish', 'French', 'Biology', 'Chemistry', 'Physics', 'Algebra',
    'Geometry', 'Calculus', 'Literature', 'Writing'
];

// Person name generators
export const person = {
    firstName: () => randomElement(firstNames),
    lastName: () => randomElement(lastNames),
    middleName: () => randomElement(firstNames),
    sex: () => randomElement(['M', 'F'])
};

// Number generators
export const number = {
    int: (options = {}) => {
        const min = options.min || 0;
        const max = options.max || 100;
        return randomInt(min, max);
    },
    float: (options = {}) => {
        const min = options.min || 0;
        const max = options.max || 100;
        const precision = options.precision || 0.01;
        const value = min + seededRandom() * (max - min);
        return Math.round(value / precision) * precision;
    }
};

// Date generators
export const date = {
    between: (options = {}) => {
        const start = new Date(options.from || '2000-01-01');
        const end = new Date(options.to || '2024-12-31');
        const diff = end.getTime() - start.getTime();
        const randomTime = start.getTime() + Math.floor(seededRandom() * diff);
        return new Date(randomTime);
    },
    birthdate: (options = {}) => {
        const currentYear = new Date().getFullYear();
        const minAge = options.min || 5;
        const maxAge = options.max || 18;
        const birthYear = currentYear - randomInt(minAge, maxAge);
        const month = randomInt(0, 11);
        const day = randomInt(1, 28); // Safe for all months
        return new Date(birthYear, month, day);
    },
    past: (options = {}) => {
        const years = options.years || 1;
        const now = new Date();
        const pastDate = new Date(now);
        pastDate.setFullYear(now.getFullYear() - randomInt(0, years));
        pastDate.setMonth(randomInt(0, 11));
        pastDate.setDate(randomInt(1, 28));
        return pastDate;
    }
};

// Location generators
export const location = {
    streetAddress: () => {
        const number = randomInt(100, 9999);
        const street = randomElement(streetNames);
        const suffix = randomElement(streetSuffixes);
        return `${number} ${street} ${suffix}`;
    },
    city: () => randomElement(cities),
    state: () => 'TX',
    zipCode: () => String(randomInt(70000, 79999)),
    buildingNumber: () => String(randomInt(100, 999)),
    secondaryAddress: () => {
        const type = randomElement(['Apt', 'Suite', 'Unit', 'Room']);
        const number = randomInt(1, 999);
        return `${type} ${number}`;
    }
};

// Company/School generators  
export const company = {
    name: () => {
        const prefix = randomElement(['Austin', 'Travis', 'Texas', 'Lone Star', 'Hill Country']);
        const type = randomElement(schoolTypes);
        return `${prefix} ${type}`;
    }
};

// Phone generator
export const phone = {
    number: (format) => {
        if (format) {
            // Replace # with random digits
            return format.replace(/#/g, () => Math.floor(seededRandom() * 10));
        }
        // Default format
        const areaCode = randomElement(['512', '737', '713', '281', '832', '346', '469', '214', '972']);
        const prefix = randomInt(200, 999);
        const suffix = randomInt(1000, 9999);
        return `${areaCode}-${prefix}-${suffix}`;
    }
};

// Internet generators
export const internet = {
    email: (options = {}) => {
        const firstName = (options.firstName || person.firstName()).toLowerCase();
        const lastName = (options.lastName || person.lastName()).toLowerCase();
        const domain = randomElement(['aisd.edu', 'school.edu', 'edfi.org']);
        return `${firstName}.${lastName}@${domain}`;
    },
    url: () => {
        const protocol = 'https';
        const domain = randomElement(['edfi.org', 'school.edu', 'aisd.edu', 'education.gov']);
        const subdomain = randomElement(['www', 'portal', 'staff', 'students', '']);
        return subdomain ? `${protocol}://${subdomain}.${domain}` : `${protocol}://${domain}`;
    }
};

// Lorem generator
export const lorem = {
    sentence: (wordCount = 10) => {
        const words = ['the', 'student', 'demonstrates', 'excellent', 'progress', 'in', 'understanding',
            'concepts', 'and', 'applying', 'knowledge', 'during', 'class', 'activities', 'shows',
            'improvement', 'with', 'consistent', 'effort', 'throughout', 'semester', 'participates',
            'actively', 'completes', 'assignments', 'timely', 'manner', 'works', 'well', 'peers'];
        
        let sentence = '';
        for (let i = 0; i < wordCount; i++) {
            sentence += randomElement(words) + ' ';
        }
        return sentence.trim().charAt(0).toUpperCase() + sentence.trim().slice(1) + '.';
    },
    
    paragraph: (sentenceCount = 3) => {
        let paragraph = '';
        for (let i = 0; i < sentenceCount; i++) {
            paragraph += lorem.sentence() + ' ';
        }
        return paragraph.trim();
    }
};

// Helpers
export const helpers = {
    arrayElement: (array) => randomElement(array),
    maybe: (callback, options = {}) => {
        const probability = options.probability || 0.5;
        return seededRandom() < probability ? callback() : undefined;
    }
};

// String generators
export const string = {
    alphanumeric: (options = {}) => {
        const length = options.length || 10;
        const casing = options.casing || 'mixed';
        const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
        let result = '';
        
        for (let i = 0; i < length; i++) {
            result += chars.charAt(Math.floor(seededRandom() * chars.length));
        }
        
        if (casing === 'lower') {
            return result.toLowerCase();
        } else if (casing === 'upper') {
            return result.toUpperCase();
        }
        return result;
    },
    numeric: (options = {}) => {
        const length = options.length || 6;
        let result = '';
        for (let i = 0; i < length; i++) {
            result += Math.floor(seededRandom() * 10);
        }
        return result;
    }
};

// Datatype generators
export const datatype = {
    boolean: (options = {}) => {
        const probability = options.probability || 0.5;
        return seededRandom() < probability;
    }
};

// Educational specific generators
export const education = {
    grade: () => randomElement(['A', 'A-', 'B+', 'B', 'B-', 'C+', 'C', 'C-', 'D', 'F']),
    gradeLevel: () => randomElement(['Kindergarten', 'First grade', 'Second grade', 'Third grade', 
        'Fourth grade', 'Fifth grade', 'Sixth grade', 'Seventh grade', 'Eighth grade',
        'Ninth grade', 'Tenth grade', 'Eleventh grade', 'Twelfth grade']),
    subject: () => randomElement(subjects),
    courseName: () => {
        const subject = randomElement(subjects);
        const level = randomElement(['Introduction to', 'Advanced', 'AP', 'Honors', 'Basic']);
        return `${level} ${subject}`;
    }
};

// Main faker object that mimics @faker-js/faker structure
export const faker = {
    seed: setSeed,
    person,
    number,
    date,
    location,
    company,
    phone,
    internet,
    lorem,
    helpers,
    string,
    datatype,
    education
};

// Default export
export default faker;