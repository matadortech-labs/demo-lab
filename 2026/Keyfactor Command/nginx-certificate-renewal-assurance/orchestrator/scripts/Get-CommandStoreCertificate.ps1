<#
.SYNOPSIS
    Retrieves a certificate from Keyfactor Command certificate store inventory.

.DESCRIPTION
    Queries a Keyfactor Command certificate store inventory endpoint and returns
    the certificate matching a requested serial number, thumbprint, or IssuedDN.

    This script carries application usage context with the Command certificate
    result so later phases can:
      - compare endpoint-served certificate data to Command inventory,
      - update Command certificate metadata,
      - populate customer-facing validation emails,
      - support multiple websites per endpoint,
      - support NGINX now and IIS later.

    This script does not update metadata in Command.

.NOTES
    Phase:
        3B

    Runtime identity:
        CORP\svc_orchestrator

    Command API identity:
        CORP\svc_validation

    Credential source:
        C:\KeyfactorScripts\EndpointValidation\Secrets\CommandApiCredential.xml

    Design principle:
        Command knows the certificate.
        Endpoint validation knows the application.
        The framework ties them together.
#>

[CmdletBinding(DefaultParameterSetName = "BySerial")]
param(
    [Parameter(Mandatory = $false)]
    [string]$CommandServer = "command.example.com",

    [Parameter(Mandatory = $false)]
    [string]$CommandStoreId = "c3aeef2b-0dc7-5d5a-948e-5fcac55eb881",

    [Parameter(Mandatory = $false)]
    [string]$CredentialPath = "C:\KeyfactorScripts\EndpointValidation\Secrets\CommandApiCredential.xml",

    [Parameter(Mandatory = $false)]
    [ValidateSet("NGINX", "IIS", "Apache", "SQLServer", "Other")]
    [string]$Platform = "NGINX",

    [Parameter(Mandatory = $false)]
    [string]$Environment = "Lab",

    [Parameter(Mandatory = $false)]
    [string]$ServerName,

    [Parameter(Mandatory = $false)]
    [string]$ApplicationName,

    [Parameter(Mandatory = $false)]
    [string]$WebsiteName,

    [Parameter(Mandatory = $false)]
    [string]$WebsiteUrl,

    [Parameter(Mandatory = $false)]
    [string]$Port = "443",

    [Parameter(Mandatory = $false)]
    [string]$ValidationSource = "Keyfactor Endpoint Validation Framework",

    [Parameter(Mandatory = $true, ParameterSetName = "BySerial")]
    [string]$SerialNumber,

    [Parameter(Mandatory = $true, ParameterSetName = "ByThumbprint")]
    [string]$Thumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = "ByIssuedDN")]
    [string]$IssuedDN,

    [Parameter(Mandatory = $false)]
    [switch]$IncludeRawInventory
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

function Normalize-Url {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    return $Value.Trim()
}

function ConvertTo-CommandCertificateObject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Certificate,

        [Parameter(Mandatory = $true)]
        $InventoryItem,

        [Parameter(Mandatory = $true)]
        [string]$CommandServer,

        [Parameter(Mandatory = $true)]
        [string]$CommandStoreId,

        [Parameter(Mandatory = $true)]
        [string]$CredentialUserName,

        [Parameter(Mandatory = $false)]
        [string]$Platform,

        [Parameter(Mandatory = $false)]
        [string]$Environment,

        [Parameter(Mandatory = $false)]
        [string]$ServerName,

        [Parameter(Mandatory = $false)]
        [string]$ApplicationName,

        [Parameter(Mandatory = $false)]
        [string]$WebsiteName,

        [Parameter(Mandatory = $false)]
        [string]$WebsiteUrl,

        [Parameter(Mandatory = $false)]
        [string]$Port,

        [Parameter(Mandatory = $false)]
        [string]$ValidationSource
    )

    return [pscustomobject]@{
        Platform                    = $Platform
        Environment                 = $Environment
        ServerName                  = $ServerName
        ApplicationName             = $ApplicationName
        WebsiteName                 = $WebsiteName
        WebsiteUrl                  = $WebsiteUrl
        WebsiteUrlNormalized        = Normalize-Url -Value $WebsiteUrl
        Port                        = $Port
        ValidationSource            = $ValidationSource

        CommandServer               = $CommandServer
        CommandStoreId              = $CommandStoreId
        CredentialUserName          = $CredentialUserName

        InventoryItemName           = $InventoryItem.Name
        CertStoreInventoryItemId    = $Certificate.CertStoreInventoryItemId
        CertificateId               = $Certificate.Id

        IssuedDN                    = $Certificate.IssuedDN
        IssuedDNNormalized          = Normalize-DistinguishedName -Value $Certificate.IssuedDN
        IssuerDN                    = $Certificate.IssuerDN
        SerialNumber                = $Certificate.SerialNumber
        SerialNumberNormalized      = Normalize-HexString -Value $Certificate.SerialNumber
        Thumbprint                  = $Certificate.Thumbprint
        ThumbprintNormalized        = Normalize-HexString -Value $Certificate.Thumbprint
        NotBefore                   = $Certificate.NotBefore
        NotAfter                    = $Certificate.NotAfter
        SigningAlgorithm            = $Certificate.SigningAlgorithm
        CertState                   = $Certificate.CertState
        ExistingMetadata            = $Certificate.Metadata
        InventoryParameters         = $InventoryItem.Parameters
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$headersScript = Join-Path $scriptRoot "New-KeyfactorApiHeaders.ps1"

if (-not (Test-Path -LiteralPath $headersScript -PathType Leaf)) {
    throw "Required helper not found: $headersScript"
}

. $headersScript

$credential = Import-CommandApiCredential -Path $CredentialPath
$headers = New-KeyfactorApiHeaders -Credential $credential

$inventoryUri = "https://$CommandServer/KeyfactorAPI/CertificateStores/$CommandStoreId/Inventory"

$response = Invoke-WebRequest `
    -Uri $inventoryUri `
    -Method Get `
    -Headers $headers `
    -ContentType "application/json" `
    -UseBasicParsing

if ($response.StatusCode -ne 200) {
    throw "Command inventory request failed. HTTP status: $($response.StatusCode)"
}

$inventory = $response.Content | ConvertFrom-Json

$targetSerial = $null
$targetThumbprint = $null
$targetIssuedDN = $null

switch ($PSCmdlet.ParameterSetName) {
    "BySerial" {
        $targetSerial = Normalize-HexString -Value $SerialNumber
    }
    "ByThumbprint" {
        $targetThumbprint = Normalize-HexString -Value $Thumbprint
    }
    "ByIssuedDN" {
        $targetIssuedDN = Normalize-DistinguishedName -Value $IssuedDN
    }
}

$matches = New-Object System.Collections.Generic.List[object]

foreach ($item in @($inventory)) {
    foreach ($cert in @($item.Certificates)) {
        $certSerial = Normalize-HexString -Value $cert.SerialNumber
        $certThumbprint = Normalize-HexString -Value $cert.Thumbprint
        $certIssuedDN = Normalize-DistinguishedName -Value $cert.IssuedDN

        $isMatch = $false

        switch ($PSCmdlet.ParameterSetName) {
            "BySerial" {
                if ($certSerial -eq $targetSerial) {
                    $isMatch = $true
                }
            }
            "ByThumbprint" {
                if ($certThumbprint -eq $targetThumbprint) {
                    $isMatch = $true
                }
            }
            "ByIssuedDN" {
                if ($certIssuedDN -eq $targetIssuedDN) {
                    $isMatch = $true
                }
            }
        }

        if ($isMatch) {
            $matches.Add((ConvertTo-CommandCertificateObject `
                -Certificate $cert `
                -InventoryItem $item `
                -CommandServer $CommandServer `
                -CommandStoreId $CommandStoreId `
                -CredentialUserName $credential.UserName `
                -Platform $Platform `
                -Environment $Environment `
                -ServerName $ServerName `
                -ApplicationName $ApplicationName `
                -WebsiteName $WebsiteName `
                -WebsiteUrl $WebsiteUrl `
                -Port $Port `
                -ValidationSource $ValidationSource))
        }
    }
}

if ($matches.Count -eq 0) {
    $searchDescription = switch ($PSCmdlet.ParameterSetName) {
        "BySerial" { "serial number '$SerialNumber'" }
        "ByThumbprint" { "thumbprint '$Thumbprint'" }
        "ByIssuedDN" { "IssuedDN '$IssuedDN'" }
    }

    throw "No certificate found in Command store inventory for $searchDescription."
}

if ($matches.Count -gt 1) {
    throw "Multiple certificates matched in Command store inventory. Match count: $($matches.Count). Refine the search by serial number or thumbprint."
}

$result = $matches[0]

if ($IncludeRawInventory) {
    $result | Add-Member -MemberType NoteProperty -Name RawInventory -Value $inventory
}

return $result