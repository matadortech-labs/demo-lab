<#
.SYNOPSIS
    Post-install smoke test template for the EndpointValidation profile.

.DESCRIPTION
    Edit the values below, then run on the target orchestrator after installing scripts,
    credentials, SSH key, and custom job extension.

    This calls the validation script directly. It does not create a Command custom job.
#>

$ValidationScript = "C:\KeyfactorScripts\EndpointValidation\Invoke-EndpointValidationProfile.ps1"

if (-not (Test-Path -LiteralPath $ValidationScript)) {
    throw "Validation script not found: $ValidationScript"
}

& $ValidationScript `
    -ValidationProfileName "nginx-web-server-tls" `
    -WorkflowInstanceId "manual-smoke-test" `
    -WorkflowDefinitionId "abb63aa3-bf6a-57db-8185-2019084aa09a" `
    -GateStepUniqueName "WaitForEndpointValidation" `
    -CertificateStoreId "<REPLACE-WITH-CERTIFICATE-STORE-ID>" `
    -ExpectedCertificateId 0 `
    -CorrelationId ([guid]::NewGuid().ToString()) `
    -TimeoutSeconds 300
