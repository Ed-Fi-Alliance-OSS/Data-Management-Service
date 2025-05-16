#!/usr/bin/env python3
import requests
import time
import json
import urllib.parse
import random
import string
from base64 import b64encode

# Configuration
config_port = 8081
dms_port = 8080
sys_admin_id = "sys-admin"
sys_admin_secret = "SdfH)98&Jk"
encoded_sys_admin_secret = urllib.parse.quote_plus(sys_admin_secret)

base_config_url = f"http://localhost:{config_port}"
base_dms_url = f"http://localhost:{dms_port}"

def create_system_admin():
    """Create a system administrator"""
    url = f"{base_config_url}/connect/register"
    payload = f"ClientId={sys_admin_id}&ClientSecret={encoded_sys_admin_secret}&DisplayName=System Administrator"
    headers = {'Content-Type': 'application/x-www-form-urlencoded'}

    response = requests.post(url, headers=headers, data=payload)
    print(f"Create system admin status: {response.status_code}")
    return response.status_code in [200, 201]

def get_config_token():
    """Get a token for the system administrator"""
    url = f"{base_config_url}/connect/token"
    payload = f"client_id={sys_admin_id}&client_secret={encoded_sys_admin_secret}&grant_type=client_credentials&scope=edfi_admin_api/full_access"
    headers = {'Content-Type': 'application/x-www-form-urlencoded'}

    response = requests.post(url, headers=headers, data=payload)
    if response.status_code == 200:
        token_data = response.json()
        config_token = token_data.get('access_token')
        print(f"Got config token: {config_token[:10]}...")
        return config_token
    else:
        print(f"Failed to get config token: {response.status_code}")
        return None

def create_vendor(config_token):
    """Create a vendor"""
    url = f"{base_config_url}/v2/vendors"
    headers = {
        'Content-Type': 'application/json',
        'Authorization': f'bearer {config_token}'
    }

    # Generate a random string for the company name
    random_suffix = ''.join(random.choices(string.ascii_letters + string.digits, k=8))
    company_name = f"Demo Vendor {random_suffix}"

    payload = {
        "company": company_name,
        "contactName": "George Washington",
        "contactEmailAddress": "george@example.com",
        "namespacePrefixes": "uri://ed-fi.org"
    }

    response = requests.post(url, headers=headers, json=payload)
    if response.status_code == 201:
        vendor_location = response.headers.get('location')
        print(f"Created vendor at: {vendor_location} with company name: {company_name}")
        return vendor_location
    else:
        print(f"Failed to create vendor: {response.status_code}")
        return None

def get_vendor(vendor_location, config_token):
    """Get vendor details"""
    headers = {'Authorization': f'bearer {config_token}'}

    response = requests.get(vendor_location, headers=headers)
    if response.status_code == 200:
        vendor_data = response.json()
        vendor_id = vendor_data.get('id')
        print(f"Got vendor ID: {vendor_id}")
        return vendor_id
    else:
        print(f"Failed to get vendor: {response.status_code}")
        return None

def create_application(vendor_id, config_token):
    """Create an application"""
    url = f"{base_config_url}/v2/applications"
    headers = {
        'Content-Type': 'application/json',
        'Authorization': f'bearer {config_token}'
    }
    payload = {
        "vendorId": vendor_id,
        "applicationName": "Demo application",
        "claimSetName": "EdFiSandbox",
        "educationOrganizationIds": [1, 2]
    }

    response = requests.post(url, headers=headers, json=payload)
    if response.status_code == 201:
        application_data = response.json()
        client_key = application_data.get('key')
        client_secret = application_data.get('secret')
        print(f"Created application with key: {client_key}")
        return client_key, client_secret
    else:
        print(f"Failed to create application: {response.status_code}")
        return None, None

def create_restricted_application(vendor_id, config_token):
    """Create an application with access only to Bayside High School"""
    url = f"{base_config_url}/v2/applications"
    headers = {
        'Content-Type': 'application/json',
        'Authorization': f'bearer {config_token}'
    }
    payload = {
        "vendorId": vendor_id,
        "applicationName": "Restricted Demo application",
        "claimSetName": "EdFiSandbox",
        "educationOrganizationIds": [1]  # Only Bayside High School
    }

    response = requests.post(url, headers=headers, json=payload)
    if response.status_code == 201:
        application_data = response.json()
        client_key = application_data.get('key')
        client_secret = application_data.get('secret')
        print(f"Created restricted application with key: {client_key}")
        return client_key, client_secret
    else:
        print(f"Failed to create restricted application: {response.status_code}")
        return None, None

def get_discovery():
    """Get discovery information"""
    url = base_dms_url

    response = requests.get(url)
    if response.status_code == 200:
        discovery_data = response.json()
        data_api = discovery_data.get('urls', {}).get('dataManagementApi')
        token_url = discovery_data.get('urls', {}).get('oauth')
        print(f"Got data API URL: {data_api}")
        print(f"Got token URL: {token_url}")
        return data_api, token_url
    else:
        print(f"Failed to get discovery info: {response.status_code}")
        return None, None

def get_dms_token(token_url, client_key, client_secret):
    """Get DMS token"""
    auth_str = f"{client_key}:{client_secret}"
    auth_bytes = auth_str.encode('ascii')
    auth_b64 = b64encode(auth_bytes).decode('ascii')

    headers = {
        'Content-Type': 'application/x-www-form-urlencoded',
        'Authorization': f'Basic {auth_b64}'
    }
    payload = "grant_type=client_credentials"

    response = requests.post(token_url, headers=headers, data=payload)
    if response.status_code == 200:
        token_data = response.json()
        dms_token = token_data.get('access_token')
        print(f"Got DMS token: {dms_token[:10]}...")
        return dms_token
    else:
        print(f"Failed to get DMS token: {response.status_code}, {response.text}")
        return None

def create_descriptors(data_api, dms_token):
    """Create required descriptors"""
    # Create education organization category descriptor
    url = f"{data_api}/ed-fi/educationOrganizationCategoryDescriptors"
    headers = {
        'Authorization': f'bearer {dms_token}',
        'Content-Type': 'application/json'
    }
    payload = {
        "namespace": "uri://ed-fi.org/educationOrganizationCategoryDescriptor",
        "codeValue": "XYZ",
        "shortDescription": "XYZ"
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create education organization category descriptor status: {response.status_code}")

    # Create grade level descriptor
    url = f"{data_api}/ed-fi/gradeLevelDescriptors"
    payload = {
        "namespace": "uri://ed-fi.org/gradeLevelDescriptor",
        "codeValue": "Tenth Grade",
        "shortDescription": "Tenth Grade"
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create grade level descriptor status: {response.status_code}")

    # Create local education agency category descriptor
    url = f"{data_api}/ed-fi/localEducationAgencyCategoryDescriptors"
    payload = {
        "namespace": "uri://ed-fi.org/localEducationAgencyCategoryDescriptor",
        "codeValue": "ABC",
        "shortDescription": "ABC"
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create local education agency category descriptor status: {response.status_code}")

def print_error_response(response):
    """Print error details for non-successful responses"""
    if response.status_code < 200 or response.status_code >= 300:
        print(f"Error response ({response.status_code}):")
        try:
            print(json.dumps(response.json(), indent=2))
        except:
            print(response.text)
        return True
    return False

def create_state_education_agency(data_api, dms_token):
    """Create state education agency"""
    url = f"{data_api}/ed-fi/stateEducationAgencies"
    headers = {
        'Authorization': f'bearer {dms_token}',
        'Content-Type': 'application/json'
    }
    payload = {
        "stateEducationAgencyId": 111,
        "nameOfInstitution": "California Department of Education",
        "categories": [
            {
                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/educationOrganizationCategoryDescriptor#XYZ"
            }
        ]
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create State Education Agency status: {response.status_code}")
    print_error_response(response)  # Print detailed error information if needed
    return 111  # Return the SEA ID for reference

def create_local_education_agency(data_api, dms_token, sea_id):
    """Create local education agency"""
    url = f"{data_api}/ed-fi/localEducationAgencies"
    headers = {
        'Authorization': f'bearer {dms_token}',
        'Content-Type': 'application/json'
    }
    payload = {
        "localEducationAgencyId": 11,
        "nameOfInstitution": "LA Public Schools",
        "categories": [
            {
                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/educationOrganizationCategoryDescriptor#XYZ"
            }
        ],
        "stateEducationAgencyReference": {
            "stateEducationAgencyId": sea_id
        },
        "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC"
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create Local Education Agency status: {response.status_code}")
    print_error_response(response)  # Print detailed error information if needed
    return 11  # Return the LEA ID for reference

def create_schools(data_api, dms_token, lea_id):
    """Create two schools - one with LEA parent, one without"""
    # Create first school with parent LEA
    url = f"{data_api}/ed-fi/schools"
    headers = {
        'Authorization': f'bearer {dms_token}',
        'Content-Type': 'application/json'
    }
    payload = {
        "schoolId": 1,
        "nameOfInstitution": "Bayside High School",
        "educationOrganizationCategories": [
            {
                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/educationOrganizationCategoryDescriptor#XYZ"
            }
        ],
        "gradeLevels": [
            {
                "gradeLevelDescriptor": "uri://ed-fi.org/gradeLevelDescriptor#Tenth Grade"
            }
        ],
        "localEducationAgencyReference": {
            "localEducationAgencyId": lea_id
        }
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create Bayside High School (with LEA parent) status: {response.status_code}")

    # Create second school without parent
    payload = {
        "schoolId": 2,
        "nameOfInstitution": "Westside High School",
        "educationOrganizationCategories": [
            {
                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/educationOrganizationCategoryDescriptor#XYZ"
            }
        ],
        "gradeLevels": [
            {
                "gradeLevelDescriptor": "uri://ed-fi.org/gradeLevelDescriptor#Tenth Grade"
            }
        ]
        # No localEducationAgencyReference - this school has no parent
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create Westside High School (no parent) status: {response.status_code}")

def create_student(data_api, dms_token):
    """Create a student"""
    url = f"{data_api}/ed-fi/students"
    headers = {
        'Authorization': f'bearer {dms_token}',
        'Content-Type': 'application/json'
    }
    payload = {
        "studentUniqueId": "604823",
        "birthDate": "2008-09-13",
        "firstName": "Lisa",
        "lastSurname": "Woods",
        "middleName": "Sybil",
        "personalTitlePrefix": "Ms",
        "preferredFirstName": "Lisarae",
        "preferredLastSurname": "Woodlock",
        "identificationDocuments": [],
        "otherNames": [],
        "personalIdentificationDocuments": [],
        "visas": []
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create student status: {response.status_code}")

def create_student_school_associations(data_api, dms_token):
    """Create student school associations"""
    url = f"{data_api}/ed-fi/studentSchoolAssociations"
    headers = {
        'Authorization': f'bearer {dms_token}',
        'Content-Type': 'application/json'
    }

    # First association
    payload = {
        "entryDate": "2021-08-23",
        "entryGradeLevelDescriptor": "uri://ed-fi.org/gradeLevelDescriptor#Tenth Grade",
        "schoolReference": {
          "schoolId": 1
        },
        "studentReference": {
          "studentUniqueId": "604823"
        }
    }

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create student association with Bayside High School status: {response.status_code}")
    print_error_response(response)  # Print detailed error information if needed

    # Second association
    payload["schoolReference"]["schoolId"] = 2

    response = requests.post(url, headers=headers, json=payload)
    print(f"Create student association with Westside High School status: {response.status_code}")
    print_error_response(response)  # Print detailed error information if needed

def test_restricted_access(data_api, restricted_token):
    """Test access using the restricted token"""
    url = f"{data_api}/ed-fi/studentSchoolAssociations"
    headers = {
        'Authorization': f'bearer {restricted_token}',
        'Content-Type': 'application/json'
    }

    response = requests.get(url, headers=headers)
    print(f"GET studentSchoolAssociations with restricted token status: {response.status_code}")

    if response.status_code == 200:
        data = response.json()
        count = len(data)
        print(f"Number of studentSchoolAssociations accessible with restricted token: {count}")

        # Print details about which associations are visible
        for i, association in enumerate(data):
            school_id = association.get("schoolReference", {}).get("schoolId", "unknown")
            student_id = association.get("studentReference", {}).get("studentUniqueId", "unknown")
            print(f"  Association {i+1}: School ID {school_id}, Student ID {student_id}")
    else:
        print_error_response(response)

def main():
    # Create system admin (may already exist)
    create_system_admin()

    # Get config token
    config_token = get_config_token()
    if not config_token:
        print("Failed to get config token. Exiting.")
        return

    # Create vendor
    vendor_location = create_vendor(config_token)
    if not vendor_location:
        print("Failed to create vendor. Exiting.")
        return

    # Get vendor details
    vendor_id = get_vendor(vendor_location, config_token)
    if not vendor_id:
        print("Failed to get vendor details. Exiting.")
        return

    # Create application with access to both schools
    client_key, client_secret = create_application(vendor_id, config_token)
    if not client_key or not client_secret:
        print("Failed to create application. Exiting.")
        return

    # Get discovery information
    data_api, token_url = get_discovery()
    if not data_api or not token_url:
        print("Failed to get discovery information. Exiting.")
        return

    # Get DMS token
    dms_token = get_dms_token(token_url, client_key, client_secret)
    if not dms_token:
        print("Failed to get DMS token. Exiting.")
        return

    # Create descriptors (may already exist)
    create_descriptors(data_api, dms_token)

    # Create education organization hierarchy
    sea_id = create_state_education_agency(data_api, dms_token)
    lea_id = create_local_education_agency(data_api, dms_token, sea_id)

    # Create schools - one under LEA, one with no parent
    create_schools(data_api, dms_token, lea_id)

    # Create student
    create_student(data_api, dms_token)

    # Create student school associations
    create_student_school_associations(data_api, dms_token)

    # Create application with access only to Bayside High School (school ID 1)
    restricted_client_key, restricted_client_secret = create_restricted_application(vendor_id, config_token)
    if restricted_client_key and restricted_client_secret:
        # Get token for restricted application
        restricted_dms_token = get_dms_token(token_url, restricted_client_key, restricted_client_secret)
        if restricted_dms_token:
            print("Successfully obtained token for restricted application")
            # Test access with restricted token
            test_restricted_access(data_api, restricted_dms_token)

    print("Multi-enrollment test completed!")

if __name__ == "__main__":
    main()
