<#
.SYNOPSIS
    Runs endpoint certificate validation for a named validation profile.

.DESCRIPTION
    Enterprise validation entry point for Keyfactor application certificate renewal assurance.

    This script is intended to be called by the Keyfactor Universal Orchestrator
    EndpointValidation custom job extension.

    It supports two modes:

      PostRenewalValidation
        Existing behavior. Validates that the endpoint is serving the renewed
        certificate, compares the live served certificate to Keyfactor Command
        inventory, updates Command metadata unless skipped, and returns the
        established compact result contract.

      PreRenewalCapture
        Captures the certificate currently served by the endpoint before renewal,
        does not update Command metadata, and returns the same top-level result
        contract with a PreviousCertificateEvidence object attached.

    The default mode is PostRenewalValidation so existing workflows and custom
    job submissions continue to behave as before.

.NOTES
    Standard portable validation profile:
      nginx-web-server-tls
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ValidationProfileName = "nginx-web-server-tls",

    [Parameter(Mandatory = $false)]
    [ValidateSet("PostRenewalValidation", "PreRenewalCapture")]
    [string]$CaptureMode = "PostRenewalValidation",

    [Parameter(Mandatory = $false)]
    [string]$CommandServer = "command.example.com",

    [Parameter(Mandatory = $false)]
    [string]$CredentialPath = "C:\KeyfactorScripts\EndpointValidation\Secrets\CommandApiCredential.xml",

    [Parameter(Mandatory = $false)]
    [int]$ValidationRetryCount = 12,

    [Parameter(Mandatory = $false)]
    [int]$ValidationRetryDelaySeconds = 15,

    [Parameter(Mandatory = $false)]
    [string]$PreRenewalCaptureScriptPath,

    [Parameter(Mandatory = $false)]
    [switch]$SkipMetadataUpdate,

    [Parameter(Mandatory = $false)]
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Convert-ToText {
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

    return ([string]$Value).Trim()
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

        if ($null -ne $property -and $null -ne $property.Value) {
            return $property.Value
        }
    }

    return $DefaultValue
}

function Test-IsInventoryLagError {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorMessage
    )

    if ($ErrorMessage -match "No certificate found in Command store inventory for serial number") {
        return $true
    }

    if ($ErrorMessage -match "Command inventory still does not contain the served certificate serial") {
        return $true
    }

    return $false
}

function New-FailureResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ValidationProfileName,

        [Parameter(Mandatory = $true)]
        [string]$FailureCategory,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage,

        [Parameter(Mandatory = $false)]
        [string]$DetailedError,

        [Parameter(Mandatory = $false)]
        [string]$CaptureMode = "PostRenewalValidation",

        [Parameter(Mandatory = $false)]
        [int]$ValidationAttemptCount = 0,

        [Parameter(Mandatory = $false)]
        [bool]$ValidationWasRetried = $false,

        [Parameter(Mandatory = $false)]
        [bool]$InventoryLagDetected = $false
    )

    return [pscustomobject]@{
        ValidationProfileName          = $ValidationProfileName
        CaptureMode                    = $CaptureMode
        ValidationStatus               = "FAIL"
        ValidationMessage              = $FailureMessage
        FailureCategory                = $FailureCategory
        DetailedError                  = $DetailedError
        ValidationAttemptCount         = $ValidationAttemptCount
        ValidationWasRetried           = $ValidationWasRetried
        InventoryLagDetected           = $InventoryLagDetected
        MetadataUpdated                = $false
        MetadataUpdateHttpStatusCode   = $null

        CertificateId                  = $null
        CertStoreInventoryItemId       = $null
        CertificateStoreId             = $null
        SerialNumber                   = $null
        Sha1Thumbprint                 = $null
        Sha256Thumbprint               = $null
        Subject                        = $null
        Issuer                         = $null
        San                            = $null
        WebsiteUrl                     = $null
        ServerName                     = $null
        Platform                       = $null
        ValidatedAtUtc                 = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

        PreviousCertificateEvidence    = $null
        CertificateChangeEvidence      = $null
    }
}

function Invoke-ValidationWithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComparisonScript,

        [Parameter(Mandatory = $true)]
        [int]$RetryCount,

        [Parameter(Mandatory = $true)]
        [int]$RetryDelaySeconds
    )

    if ($RetryCount -lt 0) {
        throw "ValidationRetryCount cannot be less than zero."
    }

    if ($RetryDelaySeconds -lt 1) {
        throw "ValidationRetryDelaySeconds must be at least 1."
    }

    $attempt = 0
    $maxAttempts = $RetryCount + 1
    $lastErrorMessage = $null
    $inventoryLagDetected = $false

    while ($attempt -lt $maxAttempts) {
        $attempt++

        Write-Host "Validation attempt $attempt of $maxAttempts..."

        try {
            $result = & $ComparisonScript

            return [pscustomobject]@{
                ValidationResult     = $result
                ValidationSucceeded  = $true
                Attempts             = $attempt
                Retried              = ($attempt -gt 1)
                LastErrorMessage     = $null
                InventoryLagDetected = $inventoryLagDetected
            }
        }
        catch {
            $lastErrorMessage = $_.Exception.Message
            $isInventoryLag = Test-IsInventoryLagError -ErrorMessage $lastErrorMessage

            if ($isInventoryLag) {
                $inventoryLagDetected = $true
            }

            if ($isInventoryLag -and $attempt -lt $maxAttempts) {
                Write-Warning "Command inventory does not yet contain the served certificate serial. Retrying in $RetryDelaySeconds seconds."
                Write-Warning $lastErrorMessage
                Start-Sleep -Seconds $RetryDelaySeconds
                continue
            }

            if ($isInventoryLag) {
                return [pscustomobject]@{
                    ValidationResult     = $null
                    ValidationSucceeded  = $false
                    Attempts             = $attempt
                    Retried              = ($attempt -gt 1)
                    LastErrorMessage     = $lastErrorMessage
                    InventoryLagDetected = $true
                }
            }

            throw
        }
    }

    return [pscustomobject]@{
        ValidationResult     = $null
        ValidationSucceeded  = $false
        Attempts             = $attempt
        Retried              = ($attempt -gt 1)
        LastErrorMessage     = $lastErrorMessage
        InventoryLagDetected = $inventoryLagDetected
    }
}

function Invoke-PreRenewalCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ValidationProfileName,

        [Parameter(Mandatory = $true)]
        [string]$CaptureScriptPath
    )

    if (-not (Test-Path -LiteralPath $CaptureScriptPath -PathType Leaf)) {
        throw "Required pre-renewal capture script not found: $CaptureScriptPath"
    }

    Write-Host "Running pre-renewal endpoint certificate capture for profile: $ValidationProfileName"

    $rawOutput = & $CaptureScriptPath `
        -ValidationProfileName $ValidationProfileName `
        -CapturePurpose "PreRenewal"

    if ($LASTEXITCODE -ne 0) {
        $joinedOutput = ($rawOutput | Out-String).Trim()
        throw "Pre-renewal capture script failed with exit code $LASTEXITCODE. Output: $joinedOutput"
    }

    $jsonText = ($rawOutput | Out-String).Trim()

    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        throw "Pre-renewal capture script returned no output."
    }

    try {
        $capture = $jsonText | ConvertFrom-Json
    }
    catch {
        throw "Pre-renewal capture script did not return valid JSON. $($_.Exception.Message)"
    }

    if ($null -eq $capture) {
        throw "Pre-renewal capture JSON was empty."
    }

    if (-not $capture.PSObject.Properties["CaptureStatus"]) {
        throw "Pre-renewal capture result did not contain CaptureStatus."
    }

    if ($capture.CaptureStatus -ne "PASS") {
        $captureError = Convert-ToText (Get-ObjectPropertyValue -Object $capture -Names @("Error") -DefaultValue "Unknown pre-renewal capture failure.")
        throw "Pre-renewal capture failed. $captureError"
    }

    $requiredFields = @(
        "SerialNumber",
        "Sha1Thumbprint",
        "Sha256Thumbprint",
        "Subject",
        "Issuer",
        "CapturedAtUtc"
    )

    foreach ($fieldName in $requiredFields) {
        $value = Get-ObjectPropertyValue -Object $capture -Names @($fieldName)

        if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
            throw "Pre-renewal capture succeeded but required field '$fieldName' was empty."
        }
    }

    return $capture
}

function New-PreRenewalCaptureResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ValidationProfileName,

        [Parameter(Mandatory = $true)]
        $Capture
    )

    $message = "Pre-renewal certificate evidence captured successfully for the application endpoint."

    $previousEvidence = [pscustomobject]@{
        CaptureStatus              = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CaptureStatus"))
        CapturePurpose             = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CapturePurpose"))
        ValidationProfileName      = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("ValidationProfileName"))
        ApplicationName            = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("ApplicationName"))
        WebsiteName                = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("WebsiteName"))
        Url                        = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Url"))
        TargetHost                 = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("TargetHost"))
        SniHost                    = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("SniHost"))
        Port                       = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Port"))
        Path                       = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Path"))
        Platform                   = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Platform"))
        Environment                = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Environment"))
        CapturedAtUtc              = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CapturedAtUtc"))
        Subject                    = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Subject"))
        Issuer                     = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Issuer"))
        CommonName                 = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CommonName"))
        San                        = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("San"))
        SerialNumber               = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("SerialNumber"))
        Sha1Thumbprint             = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Sha1Thumbprint"))
        Sha256Thumbprint           = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("Sha256Thumbprint"))
        NotBeforeUtc               = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("NotBeforeUtc"))
        NotAfterUtc                = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("NotAfterUtc"))
        TlsHandshake               = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("TlsHandshake"))
        HostnameSanMatch           = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("HostnameSanMatch"))
        CertificateNotExpired      = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CertificateNotExpired"))
        CertificateChainValidation = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CertificateChainValidation"))
        SslPolicyErrors            = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("SslPolicyErrors"))
        TlsProtocol                = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("TlsProtocol"))
        CipherAlgorithm            = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CipherAlgorithm"))
        CipherStrength             = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("CipherStrength"))
        HttpHealth                 = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("HttpHealth"))
        HttpStatusCode             = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("HttpStatusCode"))
        HttpResponse               = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("HttpResponse"))
        TestedFrom                 = Convert-ToText (Get-ObjectPropertyValue -Object $Capture -Names @("TestedFrom"))
    }

    return [pscustomobject]@{
        ValidationProfileName          = $ValidationProfileName
        CaptureMode                    = "PreRenewalCapture"
        ValidationStatus               = "PASS"
        ValidationMessage              = $message
        FailureCategory                = $null
        DetailedError                  = $null
        ValidationAttemptCount         = 1
        ValidationWasRetried           = $false
        InventoryLagDetected           = $false
        MetadataUpdated                = $false
        MetadataUpdateHttpStatusCode   = $null

        CertificateId                  = $null
        CertStoreInventoryItemId       = $null
        CertificateStoreId             = $null
        SerialNumber                   = $previousEvidence.SerialNumber
        Sha1Thumbprint                 = $previousEvidence.Sha1Thumbprint
        Sha256Thumbprint               = $previousEvidence.Sha256Thumbprint
        Subject                        = $previousEvidence.Subject
        Issuer                         = $previousEvidence.Issuer
        San                            = $previousEvidence.San
        WebsiteUrl                     = $previousEvidence.Url
        ServerName                     = $previousEvidence.TargetHost
        Platform                       = $previousEvidence.Platform
        ValidatedAtUtc                 = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

        PreviousCertificateEvidence    = $previousEvidence
        CertificateChangeEvidence      = $null
    }
}

function New-PostRenewalValidationResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ValidationProfileName,

        [Parameter(Mandatory = $true)]
        [string]$ComparisonScript,

        [Parameter(Mandatory = $true)]
        [string]$MetadataUpdateScript,

        [Parameter(Mandatory = $true)]
        [string]$CommandServer,

        [Parameter(Mandatory = $true)]
        [string]$CredentialPath,

        [Parameter(Mandatory = $true)]
        [int]$ValidationRetryCount,

        [Parameter(Mandatory = $true)]
        [int]$ValidationRetryDelaySeconds,

        [Parameter(Mandatory = $true)]
        [bool]$SkipMetadataUpdate
    )

    Write-Host "Running endpoint validation profile: $ValidationProfileName"

    $validationExecution = Invoke-ValidationWithRetry `
        -ComparisonScript $ComparisonScript `
        -RetryCount $ValidationRetryCount `
        -RetryDelaySeconds $ValidationRetryDelaySeconds

    if (-not $validationExecution.ValidationSucceeded) {
        return New-FailureResult `
            -ValidationProfileName $ValidationProfileName `
            -CaptureMode "PostRenewalValidation" `
            -FailureCategory "CommandInventoryLagTimeout" `
            -FailureMessage "Endpoint validation could not be completed because Keyfactor Command inventory did not contain the served certificate serial before the retry window expired." `
            -DetailedError $validationExecution.LastErrorMessage `
            -ValidationAttemptCount $validationExecution.Attempts `
            -ValidationWasRetried $validationExecution.Retried `
            -InventoryLagDetected $validationExecution.InventoryLagDetected
    }

    $validationResult = $validationExecution.ValidationResult

    if ($validationResult.ValidationStatus -ne "PASS") {
        return New-FailureResult `
            -ValidationProfileName $ValidationProfileName `
            -CaptureMode "PostRenewalValidation" `
            -FailureCategory "EndpointValidationFailed" `
            -FailureMessage $validationResult.ValidationMessage `
            -DetailedError $validationResult.ValidationMessage `
            -ValidationAttemptCount $validationExecution.Attempts `
            -ValidationWasRetried $validationExecution.Retried `
            -InventoryLagDetected $validationExecution.InventoryLagDetected
    }

    $metadataUpdateResult = $null
    $metadataUpdated = $false
    $metadataUpdateHttpStatusCode = $null

    if ($SkipMetadataUpdate) {
        Write-Warning "SkipMetadataUpdate was specified. Command certificate metadata will not be updated."
    }
    else {
        Write-Host "Validation passed. Updating Command certificate metadata."

        $metadataUpdateResult = & $MetadataUpdateScript `
            -CommandServer $CommandServer `
            -CredentialPath $CredentialPath `
            -Update

        if ($null -eq $metadataUpdateResult) {
            throw "Metadata update script returned no result."
        }

        if (-not $metadataUpdateResult.PSObject.Properties["MetadataUpdated"]) {
            throw "Metadata update result did not contain MetadataUpdated."
        }

        if ($metadataUpdateResult.MetadataUpdated -ne $true) {
            throw "Metadata update did not report success."
        }

        if ($metadataUpdateResult.PSObject.Properties["HttpStatusCode"]) {
            $metadataUpdateHttpStatusCode = $metadataUpdateResult.HttpStatusCode
        }

        if ($metadataUpdateHttpStatusCode -ne 204) {
            throw "Metadata update returned unexpected HTTP status code: $metadataUpdateHttpStatusCode"
        }

        $metadataUpdated = $true
    }

    $metadata = $null

    if ($metadataUpdateResult -and $metadataUpdateResult.PSObject.Properties["MetadataToWrite"]) {
        $metadata = $metadataUpdateResult.MetadataToWrite
    }

    if ($null -eq $metadata -and $validationResult.PSObject.Properties["MetadataForCommand"]) {
        $metadata = $validationResult.MetadataForCommand
    }

    return [pscustomobject]@{
        ValidationProfileName          = $ValidationProfileName
        CaptureMode                    = "PostRenewalValidation"
        ValidationStatus               = "PASS"
        ValidationMessage              = Convert-ToText (Get-ObjectPropertyValue -Object $validationResult -Names @("ValidationMessage"))
        FailureCategory                = $null
        DetailedError                  = $null
        ValidationAttemptCount         = $validationExecution.Attempts
        ValidationWasRetried           = $validationExecution.Retried
        InventoryLagDetected           = $validationExecution.InventoryLagDetected
        MetadataUpdated                = $metadataUpdated
        MetadataUpdateHttpStatusCode   = $metadataUpdateHttpStatusCode

        CertificateId                  = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_CertificateId", "CertificateId") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("CertificateId")))
        CertStoreInventoryItemId       = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_CertStoreInventoryItemId", "CertStoreInventoryItemId") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("CertStoreInventoryItemId")))
        CertificateStoreId             = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_CertificateStoreId", "CertificateStoreId") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("CommandStoreId")))

        SerialNumber                   = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_LastServedSerial", "LastServedCertificateSerial") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("ServedCertificateSerial")))
        Sha1Thumbprint                 = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_LastServedSha1", "LastServedCertificateSha1") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("ServedCertificateSha1", "CommandCertificateThumbprint")))
        Sha256Thumbprint               = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_LastServedSha256", "LastServedCertificateSha256") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("ServedCertificateSha256")))

        Subject                        = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_LastServedSubject", "LastServedCertificateSubject") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("ServedCertificateSubject")))
        Issuer                         = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_LastServedIssuer", "LastServedCertificateIssuer") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("ServedCertificateIssuer")))
        San                            = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_LastServedSan", "LastServedCertificateSan") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("ServedCertificateSan")))

        WebsiteUrl                     = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_WebsiteUrl", "WebsiteUrl") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("WebsiteUrl")))
        ServerName                     = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_Server", "ApplicationServer") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("ServerName")))
        Platform                       = Convert-ToText (Get-ObjectPropertyValue -Object $metadata -Names @("KFValidation_Platform", "ApplicationPlatform") -DefaultValue (Get-ObjectPropertyValue -Object $validationResult -Names @("Platform")))

        ValidatedAtUtc                 = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

        PreviousCertificateEvidence    = $null
        CertificateChangeEvidence      = $null
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$comparisonScript = Join-Path $scriptRoot "Compare-NginxProofToCommand.ps1"
$metadataUpdateScript = Join-Path $scriptRoot "Update-CommandCertificateValidationMetadata.ps1"

if ([string]::IsNullOrWhiteSpace($PreRenewalCaptureScriptPath)) {
    $PreRenewalCaptureScriptPath = Join-Path $scriptRoot "Get-EndpointCertificateEvidence.ps1"
}

try {
    if ($ValidationProfileName -ne "nginx-web-server-tls") {
        $result = New-FailureResult `
            -ValidationProfileName $ValidationProfileName `
            -CaptureMode $CaptureMode `
            -FailureCategory "UnknownValidationProfile" `
            -FailureMessage "Unknown validation profile '$ValidationProfileName'."
    }
    elseif ($CaptureMode -eq "PreRenewalCapture") {
        $capture = Invoke-PreRenewalCapture `
            -ValidationProfileName $ValidationProfileName `
            -CaptureScriptPath $PreRenewalCaptureScriptPath

        $result = New-PreRenewalCaptureResult `
            -ValidationProfileName $ValidationProfileName `
            -Capture $capture
    }
    else {
        if (-not (Test-Path -LiteralPath $comparisonScript -PathType Leaf)) {
            throw "Required comparison script not found: $comparisonScript"
        }

        if (-not $SkipMetadataUpdate) {
            if (-not (Test-Path -LiteralPath $metadataUpdateScript -PathType Leaf)) {
                throw "Required metadata update script not found: $metadataUpdateScript"
            }
        }

        $result = New-PostRenewalValidationResult `
            -ValidationProfileName $ValidationProfileName `
            -ComparisonScript $comparisonScript `
            -MetadataUpdateScript $metadataUpdateScript `
            -CommandServer $CommandServer `
            -CredentialPath $CredentialPath `
            -ValidationRetryCount $ValidationRetryCount `
            -ValidationRetryDelaySeconds $ValidationRetryDelaySeconds `
            -SkipMetadataUpdate ([bool]$SkipMetadataUpdate)
    }
}
catch {
    $result = New-FailureResult `
        -ValidationProfileName $ValidationProfileName `
        -CaptureMode $CaptureMode `
        -FailureCategory "ValidationExecutionError" `
        -FailureMessage "Endpoint validation profile execution failed." `
        -DetailedError $_.Exception.Message
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 30
}
else {
    $result
}
