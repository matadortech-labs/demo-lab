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

$LocalScriptPath = "C:\KeyfactorScripts\WorkflowScripts\CapturePreRenewalEndpointCertificate.Local.ps1"

if (-not (Test-Path -LiteralPath $LocalScriptPath)) {
    throw "Local pre-renewal capture script not found: $LocalScriptPath"
}

& $LocalScriptPath `
    -WorkflowInstanceId $WorkflowInstanceId `
    -WorkflowDefinitionId $WorkflowDefinitionId `
    -AgentId $AgentId `
    -CertificateStoreId $CertificateStoreId `
    -ValidationProfileName $ValidationProfileName `
    -ValidationScriptPath $ValidationScriptPath `
    -TimeoutSeconds $TimeoutSeconds `
    -PollSeconds $PollSeconds
