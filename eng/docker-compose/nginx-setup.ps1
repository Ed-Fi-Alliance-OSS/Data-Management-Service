[CmdletBinding()]
param (
    # Realm name
    [string]
    $Realm = "edfi"
)

# 1. Define the routes
$nginxFolder = "C:\nginx"
$nginxConfFile = "$nginxFolder\conf\nginx.conf"
$hostsFile = "C:\Windows\System32\drivers\etc\hosts"

# 2. Download and unzip Nginx (if not already downloaded)
$nginxDownloadUrl = "https://nginx.org/download/nginx-1.21.6.zip"
$nginxZip = "$nginxFolder\nginx.zip"

# Check if the nginx directory exists. If not, download and extract the file.
if (-Not (Test-Path $nginxFolder)) {
    Write-Host "Creating directory $nginxFolder..."
    New-Item -Path $nginxFolder -ItemType Directory
}

if (-Not (Test-Path $nginxZip)) {
    Write-Host "Downloading Nginx..."
    Invoke-WebRequest -Uri $nginxDownloadUrl -OutFile $nginxZip
} else {
    Write-Host "Nginx zip already exists."
}

Write-Host "Unzipping Nginx..."
Expand-Archive -Path $nginxZip -DestinationPath "C:\"

# After extraction, check the structure and move files correctly
$nginxExtractedFolder = "C:\nginx-1.21.6"
if (-Not (Test-Path $nginxExtractedFolder)) {
    Write-Host "Error: Nginx extraction failed or incorrect folder structure."
    exit
}

# Move the contents to the desired location
Write-Host "Moving Nginx files to $nginxFolder..."
Move-Item -Path "$nginxExtractedFolder\*" -Destination $nginxFolder -Force

# Remove the extracted folder and zip file
Remove-Item -Path $nginxZip
Remove-Item -Path $nginxExtractedFolder -Recurse

# 3. Modify the file nginx.conf
Write-Host "Modifying nginx.conf..."
$nginxConfContent = @"
events {
    worker_connections 1024;
}
http {
    server {
        listen 8080;
        server_name dms-keycloak;
        # Redirect to Keycloak
        location /realms/$Realm/protocol/openid-connect/token {
            proxy_pass http://localhost:8045;
            proxy_set_header Host `$http_host;
            proxy_set_header X-Real-IP `$remote_addr;
            proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto `$scheme;
            proxy_set_header X-Forwarded-Port 8080;
        }
        # Any other route localhost:8080 remain
        location / {
            proxy_pass http://localhost:8080;
        }
    }
}
"@
# Verify that nginx.conf exists
if (-Not (Test-Path "$nginxFolder\conf")) {
    Write-Host "Directory for nginx.conf does not exist. Aborting."
    exit
}
# Save changes in nginx.conf
Set-Content -Path $nginxConfFile -Value $nginxConfContent
Write-Host "nginx.conf successfully modified."

# 4. Modify the hosts file
Write-Host "Modifying hosts file..."
$hostsContent = Get-Content -Path $hostsFile
if ($hostsContent -notcontains "127.0.0.1 dms-keycloak") {
    Add-Content -Path $hostsFile -Value "127.0.0.1 dms-keycloak"
    Write-Host "Hosts file successfully modified."
} else {
    Write-Host "The entry already exists in the hosts file"
}

# 5. Start Nginx
Write-Host "Starting Nginx..."
if (-Not (Test-Path "$nginxFolder\nginx.exe")) {
    Write-Host "nginx.exe not found in $nginxFolder. Check Nginx extraction."
    exit
} else {
    $process = Start-Process -FilePath "$nginxFolder\nginx.exe" -NoNewWindow -PassThru -Wait
    if ($process.ExitCode -eq 0) {
        Write-Host "Nginx successfully initiated."
    } else {
        Write-Host "There was a problem with Nginx. Code: $($process.ExitCode)"
    }
}

# End
Write-Host "Script successfully completed."
