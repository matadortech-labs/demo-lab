param(
    [Parameter(Mandatory = $false)]
    [string]$ValidationProfileName = "nginx-web-server-tls",

    [Parameter(Mandatory = $false)]
    [string]$Url,

    [Parameter(Mandatory = $false)]
    [string]$SniHost,

    [Parameter(Mandatory = $false)]
    [int]$Port = 443,

    [Parameter(Mandatory = $false)]
    [string]$CapturePurpose = "PreRenewal",

    [Parameter(Mandatory = $false)]
    [int]$TcpTimeoutMilliseconds = 10000,

    [Parameter(Mandatory = $false)]
    [int]$HttpTimeoutSeconds = 15
)

$ErrorActionPreference = "Stop"

function Normalize-String {
    param(
        $Value,
        [string]$DefaultValue = ""
    )

    if ($null -eq $Value) {
        return $DefaultValue
    }

    $StringValue = [string]$Value

    if ([string]::IsNullOrWhiteSpace($StringValue)) {
        return $DefaultValue
    }

    return $StringValue.Trim()
}

function Resolve-ValidationProfile {
    param(
        [string]$ValidationProfileName,
        [string]$Url,
        [string]$SniHost,
        [int]$Port
    )

    $ProfileName = Normalize-String -Value $ValidationProfileName -DefaultValue "nginx-web-server-tls"

    if (-not [string]::IsNullOrWhiteSpace($Url)) {
        $Uri = [uri]$Url

        $ResolvedSniHost = $SniHost
        if ([string]::IsNullOrWhiteSpace($ResolvedSniHost)) {
            $ResolvedSniHost = $Uri.DnsSafeHost
        }

        $ResolvedPort = $Port
        if ($Uri.Port -gt 0) {
            $ResolvedPort = $Uri.Port
        }

        $ResolvedPath = $Uri.AbsolutePath
        if ([string]::IsNullOrWhiteSpace($ResolvedPath)) {
            $ResolvedPath = "/"
        }

        return [pscustomobject]@{
            ValidationProfileName = $ProfileName
            ApplicationName = "-"
            WebsiteName = "-"
            Url = $Uri.AbsoluteUri
            TargetHost = $Uri.DnsSafeHost
            SniHost = $ResolvedSniHost
            Port = $ResolvedPort
            Path = $ResolvedPath
            Platform = "-"
            Environment = "-"
        }
    }

    if ($ProfileName.ToLowerInvariant() -eq "nginx-web-server-tls") {
        return [pscustomobject]@{
            ValidationProfileName = "nginx-web-server-tls"
            ApplicationName = "Example NGINX Application"
            WebsiteName = "Example NGINX Site"
            Url = "https://nginx01.example.com/keyfactor/"
            TargetHost = "nginx01.example.com"
            SniHost = "nginx01.example.com"
            Port = 443
            Path = "/keyfactor/"
            Platform = "NGINX"
            Environment = "Lab"
        }
    }

    throw "Unknown validation profile '$ProfileName'. Provide -Url and -SniHost, or add the profile to this script."
}

function Convert-BytesToHex {
    param(
        [byte[]]$Bytes
    )

    if ($null -eq $Bytes) {
        return ""
    }

    $Builder = New-Object System.Text.StringBuilder

    foreach ($Byte in $Bytes) {
        [void]$Builder.Append($Byte.ToString("X2"))
    }

    return $Builder.ToString()
}

function Get-CertificateHashHex {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$Algorithm
    )

    $HashAlgorithm = [System.Security.Cryptography.HashAlgorithm]::Create($Algorithm)

    try {
        $HashBytes = $HashAlgorithm.ComputeHash($Certificate.RawData)
        return Convert-BytesToHex -Bytes $HashBytes
    }
    finally {
        if ($null -ne $HashAlgorithm) {
            $HashAlgorithm.Dispose()
        }
    }
}

function Get-CertificateDnsNames {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $Names = New-Object System.Collections.Generic.List[string]

    foreach ($Extension in $Certificate.Extensions) {
        if ($Extension.Oid.Value -eq "2.5.29.17") {
            $Formatted = $Extension.Format($false)

            if (-not [string]::IsNullOrWhiteSpace($Formatted)) {
                $Parts = $Formatted -split ","

                foreach ($Part in $Parts) {
                    $Clean = $Part.Trim()

                    if ($Clean -like "DNS Name=*") {
                        $Names.Add(($Clean -replace "^DNS Name=", "").Trim())
                    }
                    elseif ($Clean -like "DNS:*") {
                        $Names.Add(($Clean -replace "^DNS:", "").Trim())
                    }
                }
            }
        }
    }

    if ($Names.Count -eq 0) {
        return ""
    }

    return ($Names | Select-Object -Unique) -join ", "
}

function Test-HostnameMatch {
    param(
        [string]$HostName,
        [string]$CommonName,
        [string]$DnsNames
    )

    $HostLower = $HostName.ToLowerInvariant()

    if (-not [string]::IsNullOrWhiteSpace($DnsNames)) {
        $Names = $DnsNames -split ","

        foreach ($Name in $Names) {
            $Candidate = $Name.Trim().ToLowerInvariant()

            if ($Candidate -eq $HostLower) {
                return "PASS"
            }

            if ($Candidate.StartsWith("*.")) {
                $Suffix = $Candidate.Substring(1)

                if ($HostLower.EndsWith($Suffix)) {
                    return "PASS"
                }
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($CommonName)) {
        $CnLower = $CommonName.ToLowerInvariant()

        if ($CnLower -eq $HostLower) {
            return "PASS"
        }

        if ($CnLower.StartsWith("*.")) {
            $Suffix = $CnLower.Substring(1)

            if ($HostLower.EndsWith($Suffix)) {
                return "PASS"
            }
        }
    }

    return "FAIL"
}

function Get-CommonName {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    try {
        return $Certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    }
    catch {
        return ""
    }
}

function Get-EndpointCertificate {
    param(
        [string]$TargetHost,
        [string]$SniHost,
        [int]$Port,
        [int]$TcpTimeoutMilliseconds
    )

    $TcpClient = New-Object System.Net.Sockets.TcpClient

    try {
        $ConnectTask = $TcpClient.ConnectAsync($TargetHost, $Port)

        if (-not $ConnectTask.Wait($TcpTimeoutMilliseconds)) {
            throw "TCP connection to $TargetHost on port $Port timed out after $TcpTimeoutMilliseconds ms."
        }

        if (-not $TcpClient.Connected) {
            throw "TCP connection to $TargetHost on port $Port failed."
        }

        $script:RemoteCertificate = $null
        $script:SslPolicyErrors = $null
        $script:ChainStatusSummary = ""

        $Callback = {
            param(
                $Sender,
                $Certificate,
                $Chain,
                $SslPolicyErrors
            )

            if ($null -ne $Certificate) {
                $script:RemoteCertificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $Certificate
            }

            $script:SslPolicyErrors = [string]$SslPolicyErrors

            if ($null -ne $Chain -and $null -ne $Chain.ChainStatus) {
                $StatusValues = @()

                foreach ($Status in $Chain.ChainStatus) {
                    $StatusValues += (($Status.Status.ToString()) + ":" + ($Status.StatusInformation.Trim()))
                }

                $script:ChainStatusSummary = ($StatusValues -join "; ")
            }

            return $true
        }

        $NetworkStream = $TcpClient.GetStream()
        $SslStream = New-Object System.Net.Security.SslStream($NetworkStream, $false, $Callback)

        try {
            $Tls12 = [System.Security.Authentication.SslProtocols]::Tls12
            $AllowedProtocols = $Tls12

            try {
                $Tls13 = [System.Security.Authentication.SslProtocols]::Tls13
                $AllowedProtocols = $Tls12 -bor $Tls13
            }
            catch {
                $AllowedProtocols = $Tls12
            }

            try {
                $SslStream.AuthenticateAsClient(
                    $SniHost,
                    $null,
                    $AllowedProtocols,
                    $false
                )
            }
            catch {
                try {
                    if ($null -ne $SslStream) {
                        $SslStream.Dispose()
                    }

                    $NetworkStream = $TcpClient.GetStream()
                    $SslStream = New-Object System.Net.Security.SslStream($NetworkStream, $false, $Callback)

                    $SslStream.AuthenticateAsClient(
                        $SniHost,
                        $null,
                        $Tls12,
                        $false
                    )
                }
                catch {
                    throw
                }
            }

            if ($null -eq $script:RemoteCertificate) {
                throw "TLS handshake completed but no remote certificate was captured."
            }

            return [pscustomobject]@{
                Certificate = $script:RemoteCertificate
                SslPolicyErrors = $script:SslPolicyErrors
                ChainStatusSummary = $script:ChainStatusSummary
                TlsProtocol = [string]$SslStream.SslProtocol
                CipherAlgorithm = [string]$SslStream.CipherAlgorithm
                CipherStrength = [int]$SslStream.CipherStrength
            }
        }
        finally {
            if ($null -ne $SslStream) {
                $SslStream.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $TcpClient) {
            $TcpClient.Close()
        }
    }
}

function Get-HttpHealth {
    param(
        [string]$Url,
        [int]$TimeoutSeconds
    )

    try {
        $Request = [System.Net.WebRequest]::Create($Url)
        $Request.Method = "GET"
        $Request.Timeout = $TimeoutSeconds * 1000
        $Request.AllowAutoRedirect = $true

        try {
            $Response = $Request.GetResponse()
            $StatusCode = [int]$Response.StatusCode
            $StatusDescription = [string]$Response.StatusDescription
            $Response.Close()

            return [pscustomobject]@{
                Status = "PASS"
                StatusCode = $StatusCode
                Response = "$StatusCode $StatusDescription"
                Error = ""
            }
        }
        catch [System.Net.WebException] {
            if ($null -ne $_.Exception.Response) {
                $Response = $_.Exception.Response
                $StatusCode = [int]$Response.StatusCode
                $StatusDescription = [string]$Response.StatusDescription
                $Response.Close()

                return [pscustomobject]@{
                    Status = "FAIL"
                    StatusCode = $StatusCode
                    Response = "$StatusCode $StatusDescription"
                    Error = $_.Exception.Message
                }
            }

            return [pscustomobject]@{
                Status = "FAIL"
                StatusCode = 0
                Response = "-"
                Error = $_.Exception.Message
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Status = "FAIL"
            StatusCode = 0
            Response = "-"
            Error = $_.Exception.Message
        }
    }
}

$StartedAtUtc = (Get-Date).ToUniversalTime()

try {
    $Profile = Resolve-ValidationProfile `
        -ValidationProfileName $ValidationProfileName `
        -Url $Url `
        -SniHost $SniHost `
        -Port $Port

    $EndpointResult = Get-EndpointCertificate `
        -TargetHost $Profile.TargetHost `
        -SniHost $Profile.SniHost `
        -Port $Profile.Port `
        -TcpTimeoutMilliseconds $TcpTimeoutMilliseconds

    $Certificate = $EndpointResult.Certificate
    $CapturedAtUtc = (Get-Date).ToUniversalTime()

    $CommonName = Get-CommonName -Certificate $Certificate
    $DnsNames = Get-CertificateDnsNames -Certificate $Certificate
    $Sha1 = Get-CertificateHashHex -Certificate $Certificate -Algorithm "SHA1"
    $Sha256 = Get-CertificateHashHex -Certificate $Certificate -Algorithm "SHA256"

    $HostnameMatch = Test-HostnameMatch `
        -HostName $Profile.SniHost `
        -CommonName $CommonName `
        -DnsNames $DnsNames

    $NotExpired = "FAIL"

    if ($Certificate.NotAfter.ToUniversalTime() -gt (Get-Date).ToUniversalTime()) {
        $NotExpired = "PASS"
    }

    $ChainValidation = "PASS"

    if ($EndpointResult.SslPolicyErrors -ne "None") {
        $ChainValidation = "WARN"
    }

    $HttpResult = Get-HttpHealth `
        -Url $Profile.Url `
        -TimeoutSeconds $HttpTimeoutSeconds

    $Output = [ordered]@{
        CaptureStatus = "PASS"
        CapturePurpose = $CapturePurpose
        ValidationProfileName = $Profile.ValidationProfileName
        ApplicationName = $Profile.ApplicationName
        WebsiteName = $Profile.WebsiteName
        Url = $Profile.Url
        TargetHost = $Profile.TargetHost
        SniHost = $Profile.SniHost
        Port = $Profile.Port
        Path = $Profile.Path
        Platform = $Profile.Platform
        Environment = $Profile.Environment

        CapturedAtUtc = $CapturedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
        StartedAtUtc = $StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
        CompletedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")

        Subject = $Certificate.Subject
        Issuer = $Certificate.Issuer
        CommonName = $CommonName
        San = $DnsNames
        SerialNumber = $Certificate.SerialNumber
        Sha1Thumbprint = $Sha1
        Sha256Thumbprint = $Sha256
        NotBeforeUtc = $Certificate.NotBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        NotAfterUtc = $Certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

        TlsHandshake = "PASS"
        HostnameSanMatch = $HostnameMatch
        CertificateNotExpired = $NotExpired
        CertificateChainValidation = $ChainValidation
        SslPolicyErrors = $EndpointResult.SslPolicyErrors
        ChainStatus = $EndpointResult.ChainStatusSummary
        TlsProtocol = $EndpointResult.TlsProtocol
        CipherAlgorithm = $EndpointResult.CipherAlgorithm
        CipherStrength = $EndpointResult.CipherStrength

        HttpHealth = $HttpResult.Status
        HttpStatusCode = $HttpResult.StatusCode
        HttpResponse = $HttpResult.Response
        HttpError = $HttpResult.Error

        TestedFrom = $env:COMPUTERNAME
    }

    $Output | ConvertTo-Json -Depth 20
    exit 0
}
catch {
    $Output = [ordered]@{
        CaptureStatus = "FAIL"
        CapturePurpose = $CapturePurpose
        ValidationProfileName = $ValidationProfileName
        Url = $Url
        TargetHost = ""
        SniHost = $SniHost
        Port = $Port
        Path = ""
        CapturedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
        Error = $_.Exception.Message
        TestedFrom = $env:COMPUTERNAME
    }

    $Output | ConvertTo-Json -Depth 20
    exit 1
}
