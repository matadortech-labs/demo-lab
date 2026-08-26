<#
.SYNOPSIS
    Updates Keyfactor Command certificate metadata with endpoint validation evidence.

.DESCRIPTION
    Phase 5 script for the Keyfactor endpoint validation framework.

    This script:
        1. Runs Compare-NginxProofToCommand.ps1.
        2. Requires endpoint-to-Command validation to PASS.
        3. Reads ApplicationRegistry.json.
        4. Adds application ownership information.
        5. Builds KFValidation_* metadata values.
        6. Dry-runs by default.
        7. Writes metadata to Command only when -Update is specified.

    This script does not create metadata field definitions.
    Metadata fields must already exist in Keyfactor Command.

.NOTES
    Runtime identity:
        CORP\svc_orchestrator

    Command API identity:
        CORP\svc_validation

    Required helper:
        C:\KeyfactorScripts\EndpointValidation\New-KeyfactorApiHeaders.ps1

    Required comparison script:
        C:\KeyfactorScripts\EndpointValidation\Compare-NginxProofToCommand.ps1

    Required registry:
        C:\KeyfactorScripts\EndpointValidation\Config\ApplicationRegistry.json
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$CommandServer = "command.example.com",

    [Parameter(Mandatory = $false)]
    [string]$CredentialPath = "C:\KeyfactorScripts\EndpointValidation\Secrets\CommandApiCredential.xml",

    [Parameter(Mandatory = $false)]
    [string]$RegistryPath = "C:\KeyfactorScripts\EndpointValidation\Config\ApplicationRegistry.json",

    [Parameter(Mandatory = $false)]
    [string]$ComparisonScript = "C:\KeyfactorScripts\EndpointValidation\Compare-NginxProofToCommand.ps1",

    [Parameter(Mandatory = $false)]
    [switch]$Update,

    [Parameter(Mandatory = $false)]
    [switch]$RequireApplicationOwner,

    [Parameter(Mandatory = $false)]
    [switch]$IncludeRawObjects
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Import-CommandApiCredential {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Credential file not found: $Path"
    }

    $credential = Import-Clixml -Path $Path

    if (-not ($credential -is [System.Management.Automation.PSCredential])) {
        throw "Credential file did not contain a PSCredential object: $Path"
    }

    if ([string]::IsNullOrWhiteSpace($credential.UserName)) {
        throw "Imported credential username is empty."
    }

    return $credential
}

function Invoke-KeyfactorApi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("GET", "POST", "PUT")]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers,

        [Parameter(Mandatory = $false)]
        $Body
    )

    $invokeParams = @{
        Uri             = $Uri
        Method          = $Method
        Headers         = $Headers
        ContentType     = "application/json"
        UseBasicParsing = $true
    }

    if ($null -ne $Body) {
        $invokeParams.Body = ($Body | ConvertTo-Json -Depth 20)
    }

    try {
        return Invoke-WebRequest @invokeParams
    }
    catch {
        $message = $_.Exception.Message

        if ($_.Exception.Response) {
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $responseText = $reader.ReadToEnd()

                if (-not [string]::IsNullOrWhiteSpace($responseText)) {
                    $message = "$message Response: $responseText"
                }
            }
            catch {
                # Keep original message.
            }
        }

        throw "Keyfactor API call failed. Method: $Method. Uri: $Uri. Error: $message"
    }
}

function Get-ObjectPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        $Object,

        [Parameter(Mandatory = $true)]
        [string[]]$Names,

        [Parameter(Mandatory = $false)]
        $DefaultValue = $null
    )

    if ($null -eq $Object) {
        return $DefaultValue
    }

    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]

        if ($null -ne $property) {
            if ($null -ne $property.Value) {
                return $property.Value
            }
        }
    }

    return $DefaultValue
}

function Convert-ToMetadataString {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [datetime]) {
        return $Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }

    $stringValue = [string]$Value

    if ([string]::IsNullOrWhiteSpace($stringValue)) {
        return $null
    }

    return $stringValue.Trim()
}

function Convert-ToMetadataInteger {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    $stringValue = [string]$Value

    if ([string]::IsNullOrWhiteSpace($stringValue)) {
        return $null
    }

    $intValue = 0

    if ([int]::TryParse($stringValue, [ref]$intValue)) {
        return $intValue
    }

    throw "Value '$Value' could not be converted to an integer metadata value."
}

function Normalize-Url {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$Url
    )

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return $null
    }

    return $Url.Trim().TrimEnd("/").ToLowerInvariant()
}

function Read-ApplicationRegistry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Application registry file not found: $Path"
    }

    $registry = Get-Content -Path $Path -Raw | ConvertFrom-Json

    if ($null -eq $registry) {
        throw "Application registry file could not be parsed: $Path"
    }

    if (-not $registry.PSObject.Properties["Applications"]) {
        throw "Application registry does not contain an Applications array: $Path"
    }

    return $registry
}

function Find-ApplicationRegistryEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Registry,

        [Parameter(Mandatory = $true)]
        $ComparisonResult
    )

    $metadata = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("MetadataForCommand") `
        -DefaultValue $null

    $websiteUrl = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("WebsiteUrl") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadata -Names @("WebsiteUrl") -DefaultValue $null)

    $platform = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("Platform") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadata -Names @("ApplicationPlatform") -DefaultValue $null)

    $serverName = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServerName", "Server") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadata -Names @("ApplicationServer") -DefaultValue $null)

    $websiteName = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("WebsiteName") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadata -Names @("WebsiteName") -DefaultValue $null)

    $normalizedWebsiteUrl = Normalize-Url -Url $websiteUrl

    $applications = @($Registry.Applications)

    if (-not [string]::IsNullOrWhiteSpace($normalizedWebsiteUrl)) {
        $urlMatches = @(
            $applications | Where-Object {
                (Normalize-Url -Url $_.WebsiteUrl) -eq $normalizedWebsiteUrl
            }
        )

        if ($urlMatches.Count -eq 1) {
            return $urlMatches[0]
        }

        if ($urlMatches.Count -gt 1) {
            throw "Application registry contains multiple entries for WebsiteUrl '$websiteUrl'."
        }
    }

    $compoundMatches = @(
        $applications | Where-Object {
            ([string]$_.Platform).Trim().ToLowerInvariant() -eq ([string]$platform).Trim().ToLowerInvariant() -and
            ([string]$_.ServerName).Trim().ToLowerInvariant() -eq ([string]$serverName).Trim().ToLowerInvariant() -and
            ([string]$_.WebsiteName).Trim().ToLowerInvariant() -eq ([string]$websiteName).Trim().ToLowerInvariant()
        }
    )

    if ($compoundMatches.Count -eq 1) {
        return $compoundMatches[0]
    }

    if ($compoundMatches.Count -gt 1) {
        throw "Application registry contains multiple entries for Platform '$platform', ServerName '$serverName', WebsiteName '$websiteName'."
    }

    return $null
}

function Add-MetadataValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Metadata,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $false)]
        $Value,

        [Parameter(Mandatory = $false)]
        [ValidateSet("String", "Integer")]
        [string]$ValueType = "String",

        [Parameter(Mandatory = $false)]
        [switch]$AllowBlank
    )

    if ($ValueType -eq "Integer") {
        $convertedValue = Convert-ToMetadataInteger -Value $Value
    }
    else {
        $convertedValue = Convert-ToMetadataString -Value $Value
    }

    if ($null -eq $convertedValue -and -not $AllowBlank) {
        return
    }

    if ($null -eq $convertedValue -and $AllowBlank) {
        $convertedValue = ""
    }

    $Metadata[$Name] = $convertedValue
}

function Build-KFValidationMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $ComparisonResult,

        [Parameter(Mandatory = $false)]
        $ApplicationRegistryEntry
    )

    $metadataSource = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("MetadataForCommand") `
        -DefaultValue $null

    $metadata = @{}

    $platform = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("Platform") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("ApplicationPlatform") -DefaultValue $null)

    $environment = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("Environment") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("ApplicationEnvironment") -DefaultValue $null)

    $serverName = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServerName", "Server") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("ApplicationServer") -DefaultValue $null)

    $applicationName = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ApplicationName") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("ApplicationName") -DefaultValue $null)

    $websiteName = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("WebsiteName") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("WebsiteName") -DefaultValue $null)

    $websiteUrl = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("WebsiteUrl") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("WebsiteUrl") -DefaultValue $null)

    $websitePort = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("Port", "WebsitePort") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("WebsitePort") -DefaultValue $null)

    if ($ApplicationRegistryEntry) {
        $platform = Get-ObjectPropertyValue -Object $ApplicationRegistryEntry -Names @("Platform") -DefaultValue $platform
        $environment = Get-ObjectPropertyValue -Object $ApplicationRegistryEntry -Names @("Environment") -DefaultValue $environment
        $serverName = Get-ObjectPropertyValue -Object $ApplicationRegistryEntry -Names @("ServerName") -DefaultValue $serverName
        $applicationName = Get-ObjectPropertyValue -Object $ApplicationRegistryEntry -Names @("ApplicationName") -DefaultValue $applicationName
        $websiteName = Get-ObjectPropertyValue -Object $ApplicationRegistryEntry -Names @("WebsiteName") -DefaultValue $websiteName
        $websiteUrl = Get-ObjectPropertyValue -Object $ApplicationRegistryEntry -Names @("WebsiteUrl") -DefaultValue $websiteUrl
        $websitePort = Get-ObjectPropertyValue -Object $ApplicationRegistryEntry -Names @("WebsitePort", "Port") -DefaultValue $websitePort
    }

    $applicationOwner = Get-ObjectPropertyValue `
        -Object $ApplicationRegistryEntry `
        -Names @("ApplicationOwner") `
        -DefaultValue $null

    $applicationOwnerEmail = Get-ObjectPropertyValue `
        -Object $ApplicationRegistryEntry `
        -Names @("ApplicationOwnerEmail") `
        -DefaultValue $null

    $certificateStoreId = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("CommandStoreId", "CertificateStoreId") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("CertificateStoreId") -DefaultValue $null)

    $certificateId = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("CertificateId") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("CertificateId") -DefaultValue $null)

    $certStoreInventoryItemId = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("CertStoreInventoryItemId") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("CertStoreInventoryItemId") -DefaultValue $null)

    $validationStatus = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ValidationStatus") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastEndpointValidationStatus") -DefaultValue $null)

    $validationMessage = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ValidationMessage") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastEndpointValidationMessage") -DefaultValue $null)

    $validatedAtUtc = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ValidatedAtUtc") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastEndpointValidationUtc") -DefaultValue $null)

    $servedSerial = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateSerial") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateSerial") -DefaultValue $null)

    $servedSha1 = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateSha1", "ServedCertificateThumbprint") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateSha1") -DefaultValue $null)

    $servedSha256 = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateSha256") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateSha256") -DefaultValue $null)

    $servedSubject = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateSubject") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateSubject") -DefaultValue $null)

    $servedIssuer = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateIssuer") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateIssuer") -DefaultValue $null)

    $servedSan = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateSan") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateSan") -DefaultValue $null)

    $servedNotBeforeUtc = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateNotBeforeUtc") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateNotBeforeUtc") -DefaultValue $null)

    $servedNotAfterUtc = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ServedCertificateNotAfterUtc") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("LastServedCertificateNotAfterUtc") -DefaultValue $null)

    $validationSource = Get-ObjectPropertyValue `
        -Object $ComparisonResult `
        -Names @("ValidationSource") `
        -DefaultValue (Get-ObjectPropertyValue -Object $metadataSource -Names @("ValidationSource") -DefaultValue "Keyfactor Endpoint Validation Framework")

    Add-MetadataValue -Metadata $metadata -Name "KFValidation_Platform" -Value $platform
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_Environment" -Value $environment
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_Server" -Value $serverName
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_ApplicationOwner" -Value $applicationOwner
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_ApplicationOwnerEmail" -Value $applicationOwnerEmail
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_ApplicationName" -Value $applicationName
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_WebsiteName" -Value $websiteName
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_WebsiteUrl" -Value $websiteUrl
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_WebsitePort" -Value $websitePort -ValueType Integer

    Add-MetadataValue -Metadata $metadata -Name "KFValidation_CertificateStoreId" -Value $certificateStoreId
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_CertificateId" -Value $certificateId -ValueType Integer
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_CertStoreInventoryItemId" -Value $certStoreInventoryItemId -ValueType Integer

    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastStatus" -Value $validationStatus
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastMessage" -Value $validationMessage
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastValidatedUtc" -Value $validatedAtUtc

    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedSerial" -Value $servedSerial
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedSha1" -Value $servedSha1
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedSha256" -Value $servedSha256
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedSubject" -Value $servedSubject
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedIssuer" -Value $servedIssuer
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedSan" -Value $servedSan
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedNotBeforeUtc" -Value $servedNotBeforeUtc
    Add-MetadataValue -Metadata $metadata -Name "KFValidation_LastServedNotAfterUtc" -Value $servedNotAfterUtc

    Add-MetadataValue -Metadata $metadata -Name "KFValidation_Source" -Value $validationSource

    return $metadata
}

function Update-KeyfactorCertificateMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandServer,

        [Parameter(Mandatory = $true)]
        [int]$CertificateId,

        [Parameter(Mandatory = $true)]
        [hashtable]$Metadata,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers
    )

    if ($Metadata.Count -eq 0) {
        throw "Metadata payload is empty. Nothing to update."
    }

    $body = @{
        Id       = $CertificateId
        Metadata = $Metadata
    }

    $uri = "https://$CommandServer/KeyfactorAPI/Certificates/Metadata"

    $response = Invoke-KeyfactorApi `
        -Method PUT `
        -Uri $uri `
        -Headers $Headers `
        -Body $body

    return [pscustomobject]@{
        CertificateId   = $CertificateId
        HttpStatusCode  = $response.StatusCode
        ResponseContent = $response.Content
        RequestBody     = $body
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$headersScript = Join-Path $scriptRoot "New-KeyfactorApiHeaders.ps1"

if (-not (Test-Path -LiteralPath $headersScript -PathType Leaf)) {
    throw "Required helper not found: $headersScript"
}

if (-not (Test-Path -LiteralPath $ComparisonScript -PathType Leaf)) {
    throw "Required comparison script not found: $ComparisonScript"
}

. $headersScript

$validatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

Write-Host "Running endpoint-to-Command comparison..."
Write-Host "Comparison script: $ComparisonScript"
Write-Host ""

$comparisonResult = & $ComparisonScript

if ($null -eq $comparisonResult) {
    throw "Comparison script returned no result."
}

$validationStatus = Get-ObjectPropertyValue `
    -Object $comparisonResult `
    -Names @("ValidationStatus") `
    -DefaultValue $null

if ($validationStatus -ne "PASS") {
    throw "Comparison result did not PASS. Metadata will not be updated. ValidationStatus: $validationStatus"
}

$certificateIdValue = Get-ObjectPropertyValue `
    -Object $comparisonResult `
    -Names @("CertificateId") `
    -DefaultValue $null

if ($null -eq $certificateIdValue) {
    $metadataForCommand = Get-ObjectPropertyValue `
        -Object $comparisonResult `
        -Names @("MetadataForCommand") `
        -DefaultValue $null

    $certificateIdValue = Get-ObjectPropertyValue `
        -Object $metadataForCommand `
        -Names @("CertificateId") `
        -DefaultValue $null
}

$certificateId = Convert-ToMetadataInteger -Value $certificateIdValue

if ($null -eq $certificateId) {
    throw "Unable to determine Command CertificateId from comparison result."
}

Write-Host ""
Write-Host "Reading application registry..."
Write-Host "Registry path: $RegistryPath"
Write-Host ""

$registry = Read-ApplicationRegistry -Path $RegistryPath

$applicationEntry = Find-ApplicationRegistryEntry `
    -Registry $registry `
    -ComparisonResult $comparisonResult

$applicationOwnerFound = $false
$applicationOwnerWarning = $null

if ($applicationEntry) {
    $applicationOwnerFound = $true
    Write-Host "Application registry match found."
    Write-Host "Application Owner      : $($applicationEntry.ApplicationOwner)"
    Write-Host "Application Owner Email: $($applicationEntry.ApplicationOwnerEmail)"
    Write-Host ""
}
else {
    $applicationOwnerWarning = "No matching application ownership entry was found in ApplicationRegistry.json."

    if ($RequireApplicationOwner) {
        throw "$applicationOwnerWarning Metadata will not be updated because -RequireApplicationOwner was specified."
    }

    Write-Warning $applicationOwnerWarning
}

$metadataToWrite = Build-KFValidationMetadata `
    -ComparisonResult $comparisonResult `
    -ApplicationRegistryEntry $applicationEntry

if ($metadataToWrite.Count -eq 0) {
    throw "Metadata payload is empty. Nothing to update."
}

Write-Host "Metadata payload prepared."
Write-Host "Certificate ID: $certificateId"
Write-Host "Field Count   : $($metadataToWrite.Count)"
Write-Host ""

if (-not $Update) {
    $result = [pscustomobject]@{
        Phase                   = "5"
        Action                  = "CommandCertificateMetadataUpdate"
        DryRun                  = $true
        UpdateRequested         = $false
        MetadataUpdated         = $false
        CommandServer           = $CommandServer
        CredentialUserName      = $null
        CertificateId           = $certificateId
        ValidationStatus        = $validationStatus
        ApplicationOwnerFound   = $applicationOwnerFound
        ApplicationOwnerWarning = $applicationOwnerWarning
        MetadataFieldCount      = $metadataToWrite.Count
        HttpStatusCode          = $null
        Message                 = "Dry run only. Metadata was not updated. Re-run with -Update to write metadata to Command."
        ValidatedAtUtc          = $validatedAtUtc
        MetadataToWrite         = $metadataToWrite
    }

    if ($IncludeRawObjects) {
        $result | Add-Member -MemberType NoteProperty -Name ComparisonResult -Value $comparisonResult
        $result | Add-Member -MemberType NoteProperty -Name ApplicationRegistryEntry -Value $applicationEntry
    }

    return $result
}

Write-Host "Preparing Keyfactor Command API authentication..."
Write-Host ""

$credential = Import-CommandApiCredential -Path $CredentialPath
$headers = New-KeyfactorApiHeaders -Credential $credential

Write-Host "Updating certificate metadata in Keyfactor Command..."
Write-Host "Command Server : $CommandServer"
Write-Host "Credential User: $($credential.UserName)"
Write-Host "Certificate ID : $certificateId"
Write-Host ""

$updateResult = Update-KeyfactorCertificateMetadata `
    -CommandServer $CommandServer `
    -CertificateId $certificateId `
    -Metadata $metadataToWrite `
    -Headers $headers

$result = [pscustomobject]@{
    Phase                   = "5"
    Action                  = "CommandCertificateMetadataUpdate"
    DryRun                  = $false
    UpdateRequested         = $true
    MetadataUpdated         = $true
    CommandServer           = $CommandServer
    CredentialUserName      = $credential.UserName
    CertificateId           = $certificateId
    ValidationStatus        = $validationStatus
    ApplicationOwnerFound   = $applicationOwnerFound
    ApplicationOwnerWarning = $applicationOwnerWarning
    MetadataFieldCount      = $metadataToWrite.Count
    HttpStatusCode          = $updateResult.HttpStatusCode
    Message                 = "Certificate metadata was updated in Keyfactor Command."
    ValidatedAtUtc          = $validatedAtUtc
    MetadataToWrite         = $metadataToWrite
}

if ($IncludeRawObjects) {
    $result | Add-Member -MemberType NoteProperty -Name ComparisonResult -Value $comparisonResult
    $result | Add-Member -MemberType NoteProperty -Name ApplicationRegistryEntry -Value $applicationEntry
    $result | Add-Member -MemberType NoteProperty -Name UpdateResult -Value $updateResult
}

return $result