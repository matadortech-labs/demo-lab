<#
.SYNOPSIS
    Creates HTTP headers for Keyfactor Command API calls.

.DESCRIPTION
    Builds reusable Keyfactor API headers using explicit Basic authentication
    over HTTPS with a supplied PSCredential.

    Intended for the endpoint validation framework running from ORCHESTRATOR-HOST.

.NOTES
    Do not hard-code passwords in scripts.
    During build/testing, use Get-Credential.
    For demo-final automation, use protected credential storage or an approved
    secret store under the automation execution identity.
#>

function New-KeyfactorApiHeaders {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$Credential
    )

    $username = $Credential.UserName
    $password = $Credential.GetNetworkCredential().Password

    if ([string]::IsNullOrWhiteSpace($username)) {
        throw "Credential username is empty."
    }

    if ([string]::IsNullOrWhiteSpace($password)) {
        throw "Credential password is empty."
    }

    $pair = "$username`:$password"
    $encodedCreds = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes($pair))

    return @{
        "Authorization" = "Basic $encodedCreds"
        "Accept" = "application/json"
        "x-keyfactor-requested-with" = "APIClient"
    }
}