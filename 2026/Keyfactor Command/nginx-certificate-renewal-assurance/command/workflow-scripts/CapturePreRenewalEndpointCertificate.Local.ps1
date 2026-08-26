param(
    [string]$WorkflowInstanceId,
    [string]$WorkflowDefinitionId,
    [string]$AgentId,
    [string]$CertificateStoreId,
    [string]$ValidationProfileName,
    [string]$ValidationScriptPath,
    [int]$TimeoutSeconds,
    [int]$PollSeconds
)

$ErrorActionPreference = "Stop"

# CapturePreRenewalEndpointCertificate.Local.ps1
# Portable/profile-driven implementation. The uploaded Command workflow wrapper calls this file.

function Normalize-Value {
    param(
        $Value,
        [string]$DefaultValue = "-"
    )

    if ($null -eq $Value) { return $DefaultValue }
    $Text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($Text)) { return $DefaultValue }
    $Text = $Text.Trim().Trim([char]34).Trim([char]39).Trim()
    if ([string]::IsNullOrWhiteSpace($Text)) { return $DefaultValue }
    if ($Text -like '$(*') { return $DefaultValue }
    return $Text
}

function Get-ConfigValue {
    param(
        [object]$Object,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($null -eq $Object) { return $DefaultValue }
    $Property = $Object.PSObject.Properties[$Name]
    if ($null -eq $Property) { return $DefaultValue }
    if ($null -eq $Property.Value) { return $DefaultValue }
    return $Property.Value
}

function ConvertTo-ItemArray {
    param([object]$Response)

    if ($null -eq $Response) { return @() }
    if ($Response -is [System.Array]) { return @($Response) }
    if ($Response.PSObject.Properties.Name -contains "Records") { return @($Response.Records) }
    if ($Response.PSObject.Properties.Name -contains "Data") { return @($Response.Data) }
    return @($Response)
}

$ProfileName = Normalize-Value -Value $ValidationProfileName -DefaultValue "nginx-web-server-tls"
$ProfileName = $ProfileName.ToLowerInvariant()

$RegistryPath = "C:\ProgramData\Keyfactor\ExtensionData\EndpointValidation\Config\ApplicationRegistry.json"
if (-not (Test-Path -LiteralPath $RegistryPath)) {
    throw "Endpoint validation application registry was not found: $RegistryPath"
}

$Registry = Get-Content -LiteralPath $RegistryPath -Raw | ConvertFrom-Json
$Profiles = @($Registry.Profiles)
$SelectedProfile = @($Profiles | Where-Object { $_.ProfileName -eq $ProfileName })[0]

if ($null -eq $SelectedProfile) {
    throw "Validation profile '$ProfileName' was not found in $RegistryPath"
}

$CommandServer = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "CommandServer")
$ProfileAgentId = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "AgentId")
$ProfileWorkflowDefinitionId = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "WorkflowDefinitionId")
$ProfileCertificateStoreId = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "CertificateStoreId")
$ProfileValidationScriptPath = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "ValidationScriptPath")
$ProfileTimeoutSeconds = [int](Get-ConfigValue $SelectedProfile "TimeoutSeconds" 300)
$ProfilePollSeconds = [int](Get-ConfigValue $SelectedProfile "PollSeconds" 5)
$CredentialStorePath = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "CredentialStorePath")
$CredentialKeyPath = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "CredentialKeyPath")
$EvidenceCacheRoot = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "EvidenceCacheRoot")

$Platform = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "Platform")
$Environment = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "Environment")
$ServerName = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "ServerName")
$ApplicationName = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "ApplicationName")
$WebsiteName = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "WebsiteName")
$WebsiteUrl = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "WebsiteUrl")
$WebsitePort = Normalize-Value -Value (Get-ConfigValue $SelectedProfile "WebsitePort")

# Workflow parameters may override profile values, but profile is the source of truth by default.
$WorkflowDefinitionId = Normalize-Value -Value $WorkflowDefinitionId -DefaultValue $ProfileWorkflowDefinitionId
$AgentId = Normalize-Value -Value $AgentId -DefaultValue $ProfileAgentId
$CertificateStoreId = Normalize-Value -Value $CertificateStoreId -DefaultValue $ProfileCertificateStoreId
$ValidationScriptPath = Normalize-Value -Value $ValidationScriptPath -DefaultValue $ProfileValidationScriptPath

if ($TimeoutSeconds -le 0) { $TimeoutSeconds = $ProfileTimeoutSeconds }
if ($PollSeconds -le 0) { $PollSeconds = $ProfilePollSeconds }
if ($TimeoutSeconds -le 0) { $TimeoutSeconds = 300 }
if ($PollSeconds -le 0) { $PollSeconds = 5 }

if ($CommandServer -eq "-") { throw "CommandServer is missing from validation profile '$ProfileName'." }
if ($AgentId -eq "-") { throw "AgentId is missing from validation profile '$ProfileName'." }
if ($WorkflowDefinitionId -eq "-") { throw "WorkflowDefinitionId is missing from validation profile '$ProfileName'." }
if ($CertificateStoreId -eq "-") { throw "CertificateStoreId is missing from validation profile '$ProfileName'." }
if ($ValidationScriptPath -eq "-") { throw "ValidationScriptPath is missing from validation profile '$ProfileName'." }
if ($CredentialStorePath -eq "-") { throw "CredentialStorePath is missing from validation profile '$ProfileName'." }
if ($CredentialKeyPath -eq "-") { throw "CredentialKeyPath is missing from validation profile '$ProfileName'." }
if ($EvidenceCacheRoot -eq "-") { throw "EvidenceCacheRoot is missing from validation profile '$ProfileName'." }

$BaseUrl = "https://$CommandServer/KeyfactorAPI"
$RestInventoryUrl = "https://$CommandServer/KeyfactorAPI/CertificateStores/$CertificateStoreId/Inventory?PageReturned=1&ReturnLimit=10"
$CaptureMode = "PreRenewalCapture"
$StepUniqueName = "CapturePreRenewalEndpointCertificate"

# Until Command resolves WorkflowInstanceId reliably for this custom PowerShell step,
# use a generated correlation key and return it for downstream email evidence.
$WorkflowInstanceId = Normalize-Value -Value $WorkflowInstanceId -DefaultValue ""
if ([string]::IsNullOrWhiteSpace($WorkflowInstanceId)) {
    $WorkflowInstanceId = "PreRenewal-" + [guid]::NewGuid().ToString()
}
$CorrelationId = $WorkflowInstanceId

if (-not (Test-Path -LiteralPath $CredentialStorePath)) {
    throw "Command API credential record not found: $CredentialStorePath"
}
if (-not (Test-Path -LiteralPath $CredentialKeyPath)) {
    throw "Command API credential key not found: $CredentialKeyPath"
}

$KeyBytes = [System.IO.File]::ReadAllBytes($CredentialKeyPath)
$CredentialRecord = Get-Content -LiteralPath $CredentialStorePath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($CredentialRecord.Username)) {
    throw "Credential record Username is empty: $CredentialStorePath"
}
if ([string]::IsNullOrWhiteSpace($CredentialRecord.EncryptedPassword)) {
    throw "Credential record EncryptedPassword is empty: $CredentialStorePath"
}

$SecurePassword = ConvertTo-SecureString -String $CredentialRecord.EncryptedPassword -Key $KeyBytes
$Password = (New-Object System.Management.Automation.PSCredential($CredentialRecord.Username, $SecurePassword)).GetNetworkCredential().Password
if ([string]::IsNullOrWhiteSpace($Password)) {
    throw "Command API password could not be decrypted from credential store."
}

$BasicToken = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($CredentialRecord.Username + ":" + $Password))
$Headers = @{
    "Authorization" = "Basic $BasicToken"
    "x-keyfactor-requested-with" = "APIClient"
    "Accept" = "application/json"
    "Content-Type" = "application/json"
}

function Invoke-KeyfactorApi {
    param(
        [Parameter(Mandatory = $true)] [string]$Method,
        [Parameter(Mandatory = $true)] [string]$Path,
        [object]$Body = $null
    )

    $Uri = "$BaseUrl/$Path"
    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec 60
    }

    $Json = $Body | ConvertTo-Json -Depth 50
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -Body $Json -TimeoutSec 60
}

function Get-JobHistoryMatch {
    param([Parameter(Mandatory = $true)] [string]$JobId)

    $PathCandidates = @(
        "OrchestratorJobs/JobHistory?PageReturned=1&ReturnLimit=1000",
        "OrchestratorJobs/JobHistory?pq.start=0&pq.count=1000"
    )

    foreach ($Path in $PathCandidates) {
        try {
            $Response = Invoke-KeyfactorApi -Method "GET" -Path $Path
            $Items = ConvertTo-ItemArray -Response $Response
            foreach ($Item in $Items) {
                if ($Item.PSObject.Properties.Name -contains "JobId") {
                    if ([string]$Item.JobId -eq $JobId) { return $Item }
                }
            }
        }
        catch {
            # Try next path candidate.
        }
    }
    return $null
}

function Test-JobHistoryCompleted {
    param([Parameter(Mandatory = $true)] [object]$History)

    $StatusText = ""
    $ResultText = ""
    if ($History.PSObject.Properties.Name -contains "Status") { $StatusText = [string]$History.Status }
    if ($History.PSObject.Properties.Name -contains "Result") { $ResultText = [string]$History.Result }

    if ($StatusText -match "Complete|Completed") { return $true }
    if ($ResultText -match "Success|Failure|Failed") { return $true }
    return $false
}

function Test-JobHistorySuccess {
    param([Parameter(Mandatory = $true)] [object]$History)

    $ResultText = ""
    if ($History.PSObject.Properties.Name -contains "Result") { $ResultText = [string]$History.Result }
    return ($ResultText -match "Success")
}

# Confirm API authentication before submitting the custom job.
$null = Invoke-KeyfactorApi -Method "GET" -Path "MetadataFields?pq.count=1"

$JobRequest = [ordered]@{
    AgentId = $AgentId
    JobTypeName = "EndpointValidation"
    Schedule = [ordered]@{ Immediate = $true }
    JobFields = [ordered]@{
        ValidationProfileName = $ProfileName
        WorkflowInstanceId = $WorkflowInstanceId
        WorkflowDefinitionId = $WorkflowDefinitionId
        WaitStepUniqueName = $StepUniqueName
        CertificateStoreId = $CertificateStoreId
        ExpectedCertificateId = "0"
        CorrelationId = $CorrelationId
        GateStepUniqueName = $StepUniqueName
        TimeoutSeconds = "$TimeoutSeconds"
        ValidationScriptPath = $ValidationScriptPath
        CaptureMode = $CaptureMode
        RequestTimestamp = (Get-Date).ToUniversalTime().ToString("o")
    }
}

$SubmitResponse = Invoke-KeyfactorApi -Method "POST" -Path "OrchestratorJobs/Custom" -Body $JobRequest
$JobId = $null
if ($SubmitResponse.PSObject.Properties.Name -contains "JobId") { $JobId = [string]$SubmitResponse.JobId }
elseif ($SubmitResponse.PSObject.Properties.Name -contains "Id") { $JobId = [string]$SubmitResponse.Id }
if ([string]::IsNullOrWhiteSpace($JobId)) { throw "Pre-renewal capture job was submitted, but no JobId was returned." }

$Deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$FinalHistory = $null
while ((Get-Date) -lt $Deadline) {
    Start-Sleep -Seconds $PollSeconds
    $History = Get-JobHistoryMatch -JobId $JobId
    if ($null -eq $History) { continue }
    if (Test-JobHistoryCompleted -History $History) { $FinalHistory = $History; break }
}
if ($null -eq $FinalHistory) { throw "Timed out waiting for pre-renewal capture job to complete. JobId: $JobId" }
if (-not (Test-JobHistorySuccess -History $FinalHistory)) {
    $FinalStatus = ""
    $FinalResult = ""
    if ($FinalHistory.PSObject.Properties.Name -contains "Status") { $FinalStatus = [string]$FinalHistory.Status }
    if ($FinalHistory.PSObject.Properties.Name -contains "Result") { $FinalResult = [string]$FinalHistory.Result }
    throw "Pre-renewal capture job did not succeed. JobId=$JobId Status='$FinalStatus' Result='$FinalResult'"
}

# Give the Command-side handler a moment to save evidence.
Start-Sleep -Seconds 5

$CachePath = Join-Path (Join-Path $EvidenceCacheRoot "Active") "$WorkflowInstanceId.pre-renewal.json"
if (-not (Test-Path -LiteralPath $CachePath)) {
    throw "Pre-renewal capture job succeeded, but cache evidence was not found: $CachePath"
}

$Evidence = Get-Content -LiteralPath $CachePath -Raw | ConvertFrom-Json
if ($Evidence.EvidenceType -ne "PreRenewalCertificateEvidence") {
    throw "Unexpected evidence type in cache file: $($Evidence.EvidenceType)"
}
if ([string]::IsNullOrWhiteSpace($Evidence.PreviousCertificate.SerialNumber)) {
    throw "Cached pre-renewal evidence does not contain PreviousCertificate.SerialNumber."
}
if ([string]::IsNullOrWhiteSpace($Evidence.PreviousCertificate.Sha1Thumbprint)) {
    throw "Cached pre-renewal evidence does not contain PreviousCertificate.Sha1Thumbprint."
}
if ([string]::IsNullOrWhiteSpace($Evidence.PreviousCertificate.Sha256Thumbprint)) {
    throw "Cached pre-renewal evidence does not contain PreviousCertificate.Sha256Thumbprint."
}

$result = @{
    "PreRenewalCaptureStatus" = "PASS"
    "PreRenewalCaptureJobId" = $JobId
    "PreRenewalCaptureCachePath" = $CachePath
    "PreRenewalWorkflowInstanceIdUsed" = $WorkflowInstanceId
    "PreRenewalCorrelationIdUsed" = $CorrelationId
    "PreRenewalPreviousSerialNumber" = Normalize-Value -Value $Evidence.PreviousCertificate.SerialNumber
    "PreRenewalPreviousSha1Thumbprint" = Normalize-Value -Value $Evidence.PreviousCertificate.Sha1Thumbprint
    "PreRenewalPreviousSha256Thumbprint" = Normalize-Value -Value $Evidence.PreviousCertificate.Sha256Thumbprint
    "PreRenewalCapturedAtUtc" = Normalize-Value -Value $Evidence.PreviousCertificate.CapturedAtUtc

    "ValidationProfileName" = $ProfileName
    "ValidationCommandServer" = $CommandServer
    "ValidationCertificateStoreId" = $CertificateStoreId
    "ValidationRestInventoryUrl" = $RestInventoryUrl
    "ValidationProfilePlatform" = $Platform
    "ValidationProfileEnvironment" = $Environment
    "ValidationProfileServerName" = $ServerName
    "ValidationProfileApplicationName" = $ApplicationName
    "ValidationProfileWebsiteName" = $WebsiteName
    "ValidationProfileWebsiteUrl" = $WebsiteUrl
    "ValidationProfileWebsitePort" = $WebsitePort
}

return $result
