<#
.SYNOPSIS
    Collects NGINX endpoint certificate proof from a remote Linux host.

.DESCRIPTION
    Connects from ORCHESTRATOR-HOST to an NGINX server over SSH, runs the endpoint-side
    nginx-cert-status validation command, retrieves the resulting JSON proof,
    validates the proof, and returns a normalized PowerShell object.

    This script is designed for unattended workflow execution. SSH is called
    with non-interactive options so the script will fail clearly instead of
    hanging on a password prompt or host-key prompt.

.NOTES
    Runtime identity:
        CORP\svc_orchestrator

    Endpoint SSH identity:
        Linux svc_keyfactor

    Endpoint script:
        /usr/local/bin/nginx-cert-status

    Endpoint proof JSON:
        /opt/keyfactor/nginx/nginx-cert-status-last.json
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ServerName = "nginx01.example.com",

    [Parameter(Mandatory = $false)]
    [string]$SshUser = "svc_keyfactor",

    [Parameter(Mandatory = $false)]
    [string]$SshKey = "C:\KeyfactorScripts\ssh\svc_keyfactor\id_ed25519",

    [Parameter(Mandatory = $false)]
    [string]$RemoteStatusCommand = "/bin/bash -lc 'sudo -n /usr/local/bin/nginx-cert-status >/dev/null'",

    [Parameter(Mandatory = $false)]
    [string]$RemoteJsonPath = "/opt/keyfactor/nginx/nginx-cert-status-last.json",

    [Parameter(Mandatory = $false)]
    [int]$ConnectTimeoutSeconds = 10,

    [Parameter(Mandatory = $false)]
    [switch]$IncludeRawJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-LocalFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description not found: $Path"
    }
}

function Invoke-SshCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ServerName,

        [Parameter(Mandatory = $true)]
        [string]$SshUser,

        [Parameter(Mandatory = $true)]
        [string]$SshKey,

        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [int]$ConnectTimeoutSeconds
    )

    $target = "$SshUser@$ServerName"

    $sshArgs = @(
        "-i", $SshKey,
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "ConnectTimeout=$ConnectTimeoutSeconds",
        $target,
        $Command
    )

    $output = & ssh.exe @sshArgs 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        $outputText = ($output | Out-String).Trim()

        if ([string]::IsNullOrWhiteSpace($outputText)) {
            $outputText = "ssh.exe exited with code $exitCode and no output."
        }

        throw "SSH command failed. Target: $target. ExitCode: $exitCode. Output: $outputText"
    }

    return ($output | Out-String)
}

function ConvertFrom-JsonStrict {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Json
    )

    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw "JSON content is empty."
    }

    try {
        return $Json | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse endpoint proof JSON. Error: $($_.Exception.Message)"
    }
}

function Get-PropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Object,

        [Parameter(Mandatory = $true)]
        [string[]]$Path
    )

    $current = $Object

    foreach ($part in $Path) {
        if ($null -eq $current) {
            return $null
        }

        $property = $current.PSObject.Properties[$part]

        if ($null -eq $property) {
            return $null
        }

        $current = $property.Value
    }

    return $current
}

function Get-FirstAvailablePropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Object,

        [Parameter(Mandatory = $true)]
        [string[][]]$CandidatePaths
    )

    foreach ($path in $CandidatePaths) {
        $value = Get-PropertyValue -Object $Object -Path $path

        if ($null -ne $value) {
            $valueAsString = [string]$value

            if (-not [string]::IsNullOrWhiteSpace($valueAsString)) {
                return $value
            }
        }
    }

    return $null
}

function Assert-NginxProof {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Proof,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedServerName
    )

    $validationType = Get-PropertyValue -Object $Proof -Path @("ValidationType")
    $platform = Get-PropertyValue -Object $Proof -Path @("Platform")
    $serverName = Get-PropertyValue -Object $Proof -Path @("Server", "ServerName")
    $status = Get-PropertyValue -Object $Proof -Path @("ValidationStatus")
    $servedSerial = Get-PropertyValue -Object $Proof -Path @("ServedCertificate", "SerialNumber")
    $servedSha1 = Get-PropertyValue -Object $Proof -Path @("ServedCertificate", "Sha1Thumbprint")
    $servedSha256 = Get-PropertyValue -Object $Proof -Path @("ServedCertificate", "Sha256Thumbprint")
    $nginxState = Get-PropertyValue -Object $Proof -Path @("Application", "ServiceState")

    if ($validationType -ne "CertificateRenewalProof") {
        throw "Unexpected endpoint proof ValidationType: $validationType"
    }

    if ($platform -ne "NGINX") {
        throw "Unexpected endpoint proof Platform: $platform"
    }

    if ($serverName -ne $ExpectedServerName) {
        throw "Endpoint proof Server.ServerName '$serverName' did not match expected '$ExpectedServerName'."
    }

    if ($status -ne "PASS") {
        $message = Get-PropertyValue -Object $Proof -Path @("ValidationMessage")
        throw "Endpoint proof status is '$status'. Message: $message"
    }

    if ([string]::IsNullOrWhiteSpace($servedSerial)) {
        throw "Endpoint proof is missing ServedCertificate.SerialNumber."
    }

    if ([string]::IsNullOrWhiteSpace($servedSha1)) {
        throw "Endpoint proof is missing ServedCertificate.Sha1Thumbprint."
    }

    if ([string]::IsNullOrWhiteSpace($servedSha256)) {
        throw "Endpoint proof is missing ServedCertificate.Sha256Thumbprint."
    }

    if ($nginxState -ne "active") {
        throw "NGINX service state is '$nginxState', expected 'active'."
    }

    $comparisonPaths = @(
        @("Comparisons", "StagedVsActiveSerial"),
        @("Comparisons", "ActiveVsServedSerial"),
        @("Comparisons", "StagedVsActiveSha256"),
        @("Comparisons", "ActiveVsServedSha256")
    )

    foreach ($path in $comparisonPaths) {
        $comparisonValue = Get-PropertyValue -Object $Proof -Path $path
        $comparisonName = $path[-1]

        if ($comparisonValue -ne "PASS") {
            throw "Endpoint proof comparison '$comparisonName' is '$comparisonValue', expected PASS."
        }
    }
}

Assert-LocalFile -Path $SshKey -Description "SSH private key"

Write-Host "Running endpoint validation command on $ServerName..."

[void](Invoke-SshCommand `
    -ServerName $ServerName `
    -SshUser $SshUser `
    -SshKey $SshKey `
    -Command $RemoteStatusCommand `
    -ConnectTimeoutSeconds $ConnectTimeoutSeconds)

Write-Host "Endpoint validation command completed."
Write-Host "Retrieving endpoint proof JSON from $ServerName..."

$jsonCommand = "cat $RemoteJsonPath"

$jsonContent = Invoke-SshCommand `
    -ServerName $ServerName `
    -SshUser $SshUser `
    -SshKey $SshKey `
    -Command $jsonCommand `
    -ConnectTimeoutSeconds $ConnectTimeoutSeconds

$proof = ConvertFrom-JsonStrict -Json $jsonContent

Assert-NginxProof -Proof $proof -ExpectedServerName $ServerName

$scriptVersion = Get-PropertyValue -Object $proof -Path @("Script", "Version")
$endpointUrl = Get-PropertyValue -Object $proof -Path @("Endpoint", "Url")
$applicationName = Get-PropertyValue -Object $proof -Path @("Application", "Name")
$applicationVersion = Get-PropertyValue -Object $proof -Path @("Application", "Version")
$applicationState = Get-PropertyValue -Object $proof -Path @("Application", "ServiceState")

$stagedSerial = Get-PropertyValue -Object $proof -Path @("StagedCertificate", "SerialNumber")
$activeSerial = Get-PropertyValue -Object $proof -Path @("ActiveCertificate", "SerialNumber")
$servedSerial = Get-PropertyValue -Object $proof -Path @("ServedCertificate", "SerialNumber")

$stagedSha1 = Get-PropertyValue -Object $proof -Path @("StagedCertificate", "Sha1Thumbprint")
$activeSha1 = Get-PropertyValue -Object $proof -Path @("ActiveCertificate", "Sha1Thumbprint")
$servedSha1 = Get-PropertyValue -Object $proof -Path @("ServedCertificate", "Sha1Thumbprint")

$stagedSha256 = Get-PropertyValue -Object $proof -Path @("StagedCertificate", "Sha256Thumbprint")
$activeSha256 = Get-PropertyValue -Object $proof -Path @("ActiveCertificate", "Sha256Thumbprint")
$servedSha256 = Get-PropertyValue -Object $proof -Path @("ServedCertificate", "Sha256Thumbprint")

$servedSan = Get-FirstAvailablePropertyValue -Object $proof -CandidatePaths @(
    @("ServedCertificate", "SubjectAlternativeNames"),
    @("ServedCertificate", "SAN"),
    @("ServedCertificate", "San"),
    @("ServedCertificate", "SubjectAlternativeName"),
    @("ServedCertificate", "SubjectAltName"),
    @("ServedCertificate", "DnsNames")
)

$result = [pscustomobject]@{
    EndpointValidationStatus       = Get-PropertyValue -Object $proof -Path @("ValidationStatus")
    EndpointValidationMessage      = Get-PropertyValue -Object $proof -Path @("ValidationMessage")
    Platform                       = Get-PropertyValue -Object $proof -Path @("Platform")
    ScriptVersion                  = $scriptVersion
    ServerName                     = Get-PropertyValue -Object $proof -Path @("Server", "ServerName")
    EndpointUrl                    = $endpointUrl
    ApplicationName                = $applicationName
    ApplicationVersion             = $applicationVersion
    ApplicationServiceState        = $applicationState
    ValidatedAtUtc                 = Get-PropertyValue -Object $proof -Path @("ValidatedAtUtc")

    StagedCertificateSerial        = $stagedSerial
    ActiveCertificateSerial        = $activeSerial
    ServedCertificateSerial        = $servedSerial

    StagedCertificateSha1          = $stagedSha1
    ActiveCertificateSha1          = $activeSha1
    ServedCertificateSha1          = $servedSha1

    StagedCertificateSha256        = $stagedSha256
    ActiveCertificateSha256        = $activeSha256
    ServedCertificateSha256        = $servedSha256

    ServedCertificateSubject       = Get-PropertyValue -Object $proof -Path @("ServedCertificate", "Subject")
    ServedCertificateIssuer        = Get-PropertyValue -Object $proof -Path @("ServedCertificate", "Issuer")
    ServedCertificateSan           = $servedSan
    ServedCertificateNotBeforeUtc  = Get-PropertyValue -Object $proof -Path @("ServedCertificate", "NotBeforeUtc")
    ServedCertificateNotAfterUtc   = Get-PropertyValue -Object $proof -Path @("ServedCertificate", "NotAfterUtc")

    StagedVsActiveSerial           = Get-PropertyValue -Object $proof -Path @("Comparisons", "StagedVsActiveSerial")
    ActiveVsServedSerial           = Get-PropertyValue -Object $proof -Path @("Comparisons", "ActiveVsServedSerial")
    StagedVsActiveSha256           = Get-PropertyValue -Object $proof -Path @("Comparisons", "StagedVsActiveSha256")
    ActiveVsServedSha256           = Get-PropertyValue -Object $proof -Path @("Comparisons", "ActiveVsServedSha256")

    RawProof                       = $proof
}

Write-Host ""
Write-Host "NGINX endpoint proof collection succeeded."
Write-Host "Server       : $($result.ServerName)"
Write-Host "Status       : $($result.EndpointValidationStatus)"
Write-Host "Served Serial: $($result.ServedCertificateSerial)"
Write-Host "Served SHA1  : $($result.ServedCertificateSha1)"
Write-Host "Served SHA256: $($result.ServedCertificateSha256)"
Write-Host "Served SAN   : $($result.ServedCertificateSan)"
Write-Host "Valid To UTC : $($result.ServedCertificateNotAfterUtc)"
Write-Host ""

if ($IncludeRawJson) {
    $result | Add-Member -MemberType NoteProperty -Name RawJson -Value $jsonContent
}

return $result