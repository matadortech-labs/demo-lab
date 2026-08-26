<#
.SYNOPSIS
    Compares NGINX endpoint certificate proof to Keyfactor Command inventory.

.DESCRIPTION
    Runs endpoint proof collection against an NGINX server, retrieves the
    matching certificate from Keyfactor Command certificate store inventory,
    and compares the live served certificate to the Command inventory record.

    This script produces a final validation object suitable for:
      - workflow decisioning,
      - confirmation/validation email,
      - later Command metadata enrichment,
      - audit evidence.

    This script does not update Command metadata.

.NOTES
    Phase:
        3C

    Runtime identity:
        CORP\svc_orchestrator

    Command API identity:
        CORP\svc_validation

    Endpoint SSH identity:
        Linux svc_keyfactor

    Design principle:
        Command knows the certificate.
        Endpoint validation knows the application.
        The framework ties them together.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("NGINX")]
    [string]$Platform = "NGINX",

    [Parameter(Mandatory = $false)]
    [string]$Environment = "Lab",

    [Parameter(Mandatory = $false)]
    [string]$ServerName = "nginx01.example.com",

    [Parameter(Mandatory = $false)]
    [string]$ApplicationName = "keyfactor",

    [Parameter(Mandatory = $false)]
    [string]$WebsiteName = "keyfactor",

    [Parameter(Mandatory = $false)]
    [string]$WebsiteUrl = "https://nginx01.example.com/keyfactor/",

    [Parameter(Mandatory = $false)]
    [string]$Port = "443",

    [Parameter(Mandatory = $false)]
    [string]$CommandServer = "command.example.com",

    [Parameter(Mandatory = $false)]
    [string]$CommandStoreId = "c3aeef2b-0dc7-5d5a-948e-5fcac55eb881",

    [Parameter(Mandatory = $false)]
    [string]$CredentialPath = "C:\KeyfactorScripts\EndpointValidation\Secrets\CommandApiCredential.xml",

    [Parameter(Mandatory = $false)]
    [string]$SshUser = "svc_keyfactor",

    [Parameter(Mandatory = $false)]
    [string]$SshKey = "C:\KeyfactorScripts\ssh\svc_keyfactor\id_ed25519",

    [Parameter(Mandatory = $false)]
    [string]$ValidationSource = "Keyfactor Endpoint Validation Framework",

    [Parameter(Mandatory = $false)]
    [switch]$IncludeRawObjects
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Normalize-HexString {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    return (($Value -replace ":", "") -replace "\s", "").ToUpperInvariant()
}

function Normalize-DistinguishedName {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $normalized = $Value.Trim()
    $normalized = $normalized -replace "\s*=\s*", "="
    $normalized = $normalized -replace "\s*,\s*", ","

    return $normalized.ToUpperInvariant()
}

function ConvertTo-IsoString {
    [CmdletBinding()]
    param(
        [AllowNull()]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    return ([string]$Value).Trim()
}

function New-ComparisonResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [AllowNull()]
        [string]$EndpointValue,

        [AllowNull()]
        [string]$CommandValue
    )

    $status = if ($EndpointValue -eq $CommandValue) { "PASS" } else { "FAIL" }

    return [pscustomobject]@{
        Name          = $Name
        Status        = $status
        EndpointValue = $EndpointValue
        CommandValue  = $CommandValue
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

function Get-FirstPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Object,

        [Parameter(Mandatory = $true)]
        [string[][]]$CandidatePaths
    )

    foreach ($path in $CandidatePaths) {
        $value = Get-PropertyValue -Object $Object -Path $path

        if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
            return $value
        }
    }

    return $null
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$getNginxProofScript = Join-Path $scriptRoot "Get-NginxEndpointProof.ps1"
$getCommandCertScript = Join-Path $scriptRoot "Get-CommandStoreCertificate.ps1"

if (-not (Test-Path -LiteralPath $getNginxProofScript -PathType Leaf)) {
    throw "Required script not found: $getNginxProofScript"
}

if (-not (Test-Path -LiteralPath $getCommandCertScript -PathType Leaf)) {
    throw "Required script not found: $getCommandCertScript"
}

$validatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

Write-Host "Collecting NGINX endpoint proof..."
Write-Host "Server      : $ServerName"
Write-Host "Website URL : $WebsiteUrl"
Write-Host ""

$endpointProof = & $getNginxProofScript `
    -ServerName $ServerName `
    -SshUser $SshUser `
    -SshKey $SshKey

$endpointStatus = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("EndpointValidationStatus"),
    @("ValidationStatus"),
    @("Status"),
    @("RawProof", "ValidationStatus")
)

$endpointMessage = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("EndpointValidationMessage"),
    @("ValidationMessage"),
    @("Message"),
    @("RawProof", "ValidationMessage")
)

$servedSerial = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateSerial"),
    @("RawProof", "ServedCertificate", "SerialNumber"),
    @("RawProof", "ServedCertificate", "Serial"),
    @("ServedCertificate", "SerialNumber"),
    @("ServedCertificate", "Serial")
)

$servedSha1 = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateSha1"),
    @("ServedCertificateThumbprint"),
    @("RawProof", "ServedCertificate", "Sha1Thumbprint"),
    @("RawProof", "ServedCertificate", "SHA1Thumbprint"),
    @("RawProof", "ServedCertificate", "Thumbprint"),
    @("ServedCertificate", "Sha1Thumbprint"),
    @("ServedCertificate", "SHA1Thumbprint"),
    @("ServedCertificate", "Thumbprint")
)

$servedSha256 = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateSha256"),
    @("RawProof", "ServedCertificate", "Sha256Thumbprint"),
    @("RawProof", "ServedCertificate", "SHA256Thumbprint"),
    @("ServedCertificate", "Sha256Thumbprint"),
    @("ServedCertificate", "SHA256Thumbprint")
)

$servedSubject = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateSubject"),
    @("RawProof", "ServedCertificate", "Subject"),
    @("ServedCertificate", "Subject")
)

$servedIssuer = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateIssuer"),
    @("RawProof", "ServedCertificate", "Issuer"),
    @("ServedCertificate", "Issuer")
)

$servedSan = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateSan"),
    @("RawProof", "ServedCertificate", "SAN"),
    @("RawProof", "ServedCertificate", "San"),
    @("ServedCertificate", "SAN"),
    @("ServedCertificate", "San")
)

$servedNotBefore = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateNotBeforeUtc"),
    @("RawProof", "ServedCertificate", "NotBeforeUtc"),
    @("ServedCertificate", "NotBeforeUtc")
)

$servedNotAfter = Get-FirstPropertyValue -Object $endpointProof -CandidatePaths @(
    @("ServedCertificateNotAfterUtc"),
    @("RawProof", "ServedCertificate", "NotAfterUtc"),
    @("ServedCertificate", "NotAfterUtc")
)

if ([string]::IsNullOrWhiteSpace([string]$endpointStatus)) {
    throw "Endpoint proof did not include a validation status."
}

if ([string]::IsNullOrWhiteSpace([string]$servedSerial)) {
    throw "Endpoint proof did not include served certificate serial."
}

Write-Host "Retrieving matching certificate from Command inventory..."
Write-Host "Served serial: $servedSerial"
Write-Host ""

$commandCert = & $getCommandCertScript `
    -Platform $Platform `
    -Environment $Environment `
    -ServerName $ServerName `
    -ApplicationName $ApplicationName `
    -WebsiteName $WebsiteName `
    -WebsiteUrl $WebsiteUrl `
    -Port $Port `
    -CommandServer $CommandServer `
    -CommandStoreId $CommandStoreId `
    -CredentialPath $CredentialPath `
    -ValidationSource $ValidationSource `
    -SerialNumber $servedSerial

$endpointSerialNormalized = Normalize-HexString -Value $servedSerial
$commandSerialNormalized = Normalize-HexString -Value $commandCert.SerialNumber

$endpointSha1Normalized = Normalize-HexString -Value $servedSha1
$commandThumbprintNormalized = Normalize-HexString -Value $commandCert.Thumbprint

$endpointSubjectNormalized = Normalize-DistinguishedName -Value $servedSubject
$commandIssuedDNNormalized = Normalize-DistinguishedName -Value $commandCert.IssuedDN

$endpointNotAfterNormalized = ConvertTo-IsoString -Value $servedNotAfter
$commandNotAfterNormalized = ConvertTo-IsoString -Value $commandCert.NotAfter

$comparisons = @(
    New-ComparisonResult `
        -Name "Served Serial equals Command Serial" `
        -EndpointValue $endpointSerialNormalized `
        -CommandValue $commandSerialNormalized

    New-ComparisonResult `
        -Name "Served SHA1 equals Command Thumbprint" `
        -EndpointValue $endpointSha1Normalized `
        -CommandValue $commandThumbprintNormalized

    New-ComparisonResult `
        -Name "Served Subject equals Command IssuedDN" `
        -EndpointValue $endpointSubjectNormalized `
        -CommandValue $commandIssuedDNNormalized

    New-ComparisonResult `
        -Name "Served NotAfter equals Command NotAfter" `
        -EndpointValue $endpointNotAfterNormalized `
        -CommandValue $commandNotAfterNormalized
)

$failedComparisons = @($comparisons | Where-Object { $_.Status -ne "PASS" })

$endpointProofPassed = ([string]$endpointStatus -eq "PASS")
$commandMatchPassed = ($failedComparisons.Count -eq 0)

$overallStatus = if ($endpointProofPassed -and $commandMatchPassed) {
    "PASS"
}
else {
    "FAIL"
}

$overallMessage = if ($overallStatus -eq "PASS") {
    "The certificate served by the application URL matches the certificate inventoried in Keyfactor Command."
}
else {
    $failureMessages = New-Object System.Collections.Generic.List[string]

    if (-not $endpointProofPassed) {
        $failureMessages.Add("Endpoint proof status was '$endpointStatus'.")
    }

    foreach ($comparison in $failedComparisons) {
        $failureMessages.Add("$($comparison.Name) failed.")
    }

    ($failureMessages -join " ")
}

$result = [pscustomobject]@{
    ValidationType                 = "EndpointCertificateCommandInventoryComparison"
    ValidationStatus               = $overallStatus
    ValidationMessage              = $overallMessage
    ValidatedAtUtc                 = $validatedAtUtc
    ValidationSource               = $ValidationSource

    Platform                       = $Platform
    Environment                    = $Environment
    ServerName                     = $ServerName
    ApplicationName                = $ApplicationName
    WebsiteName                    = $WebsiteName
    WebsiteUrl                     = $WebsiteUrl
    Port                           = $Port

    EndpointProofStatus            = $endpointStatus
    EndpointProofMessage           = $endpointMessage
    CommandInventoryMatchStatus    = if ($commandMatchPassed) { "PASS" } else { "FAIL" }

    CommandServer                  = $CommandServer
    CommandStoreId                 = $CommandStoreId
    CertificateId                  = $commandCert.CertificateId
    CertStoreInventoryItemId       = $commandCert.CertStoreInventoryItemId

    ServedCertificateSerial        = $servedSerial
    CommandCertificateSerial       = $commandCert.SerialNumber

    ServedCertificateSha1          = $servedSha1
    CommandCertificateThumbprint   = $commandCert.Thumbprint

    ServedCertificateSha256        = $servedSha256

    ServedCertificateSubject       = $servedSubject
    CommandCertificateIssuedDN     = $commandCert.IssuedDN

    ServedCertificateIssuer        = $servedIssuer
    CommandCertificateIssuerDN     = $commandCert.IssuerDN

    ServedCertificateSan           = $servedSan
    ServedCertificateNotBeforeUtc  = $servedNotBefore
    ServedCertificateNotAfterUtc   = $servedNotAfter
    CommandCertificateNotBeforeUtc = $commandCert.NotBefore
    CommandCertificateNotAfterUtc  = $commandCert.NotAfter

    Comparisons                    = $comparisons

    MetadataForCommand             = [pscustomobject]@{
        ApplicationPlatform              = $Platform
        ApplicationEnvironment           = $Environment
        ApplicationServer                = $ServerName
        ApplicationName                  = $ApplicationName
        WebsiteName                      = $WebsiteName
        WebsiteUrl                       = $WebsiteUrl
        WebsitePort                      = $Port
        CertificateStoreId               = $CommandStoreId
        CertificateId                    = $commandCert.CertificateId
        CertStoreInventoryItemId         = $commandCert.CertStoreInventoryItemId
        LastEndpointValidationStatus     = $overallStatus
        LastEndpointValidationMessage    = $overallMessage
        LastEndpointValidationUtc        = $validatedAtUtc
        LastServedCertificateSerial      = $servedSerial
        LastServedCertificateSha1        = $servedSha1
        LastServedCertificateSha256      = $servedSha256
        LastServedCertificateSubject     = $servedSubject
        LastServedCertificateIssuer      = $servedIssuer
        LastServedCertificateSan         = $servedSan
        LastServedCertificateNotBeforeUtc = $servedNotBefore
        LastServedCertificateNotAfterUtc = $servedNotAfter
        ValidationSource                 = $ValidationSource
    }
}

if ($IncludeRawObjects) {
    $result | Add-Member -MemberType NoteProperty -Name EndpointProof -Value $endpointProof
    $result | Add-Member -MemberType NoteProperty -Name CommandCertificate -Value $commandCert
}

return $result