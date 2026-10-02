<#
  FullUI one-file installer (Windows PowerShell 5.1 and PowerShell 7+).

  Drives your RUNNING Jellyfin server through its own web API: adds the plugin
  repositories, installs File Transformation + FullUI, restarts Jellyfin, checks
  everything is active, optionally configures TMDB / Ollama, then self-tests.
  Safe to run again at any time.

  Normal users never run this directly: double-click Install-FullUI.cmd (Windows)
  or run install.sh (Linux/macOS).

  Advanced switches:
    -Unattended  no questions (needs -Username/-Password or -ApiKey; prefer the environment variables
                 FULLUI_PASSWORD / FULLUI_APIKEY / FULLUI_TMDB_KEY so secrets stay out of the command line)
    -Update      only update FullUI / File Transformation if a newer version exists
    -Uninstall   remove FullUI (and optionally File Transformation)
    -Insecure    accept the self-signed HTTPS certificate of YOUR Jellyfin server (that host only)
#>
[CmdletBinding()]
param(
    [string]$Server,
    [string]$Username,
    [string]$Password,
    [string]$ApiKey,
    [switch]$Unattended,
    [switch]$Update,
    [switch]$Uninstall,
    [switch]$RemoveFileTransformation,
    [switch]$PurgeData,
    [string]$ServerName,
    [string]$TmdbKey,
    [switch]$InstallOllama,
    [switch]$SkipOllama,
    [switch]$AllowOtherVersion,
    [switch]$Insecure,
    [string]$FireTvIp,
    [string]$LogDir,
    [int]$RestartTimeoutSec = 300,
    [int]$InstallTimeoutSec = 300,
    [int]$PollSec = 2,
    [string]$FileTransformationRepoUrl = 'https://www.iamparadox.dev/jellyfin/plugins/manifest.json',
    [string]$FullUIRepoUrl = 'https://raw.githubusercontent.com/Narsmow/FullUISuit/main/manifest.json',
    [string]$FullUIRepoFallbackUrl = 'https://github.com/Narsmow/FullUISuit/releases/latest/download/manifest.json',
    [string]$TmdbBaseUrl = 'https://api.themoviedb.org/3',
    [string]$ApkUrl = 'https://github.com/Narsmow/FullUISuit/releases/download/firetv-latest/FullUI-release.apk'
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 draws a progress bar for Invoke-WebRequest that makes big downloads (Ollama, ~1 GB) extremely slow.
$ProgressPreference = 'SilentlyContinue'
$InstallerVersion = '1.1.0'
$FullUIGuid = '7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57'
$FtGuid = '5e87cc92-571a-4d8d-8d98-d2d4147f9f90'
$script:Total = 9
$script:LogFile = $null
$script:Secrets = New-Object System.Collections.ArrayList
$script:Token = $null
$script:UsedPassword = $false
$script:Base = $null
$script:ServerVersion = $null
$script:Done = $false
$script:Reported = $false
$script:Insecure = [bool]$Insecure
$script:InsecureHost = $null   # certificate checking is skipped for this one host name only

# ---------------------------------------------------------------- environment
if (-not $Password -and $env:FULLUI_PASSWORD) { $Password = $env:FULLUI_PASSWORD }
if (-not $ApiKey -and $env:FULLUI_APIKEY) { $ApiKey = $env:FULLUI_APIKEY }
if (-not $TmdbKey -and $env:FULLUI_TMDB_KEY) { $TmdbKey = $env:FULLUI_TMDB_KEY }
$IsWin = ($env:OS -eq 'Windows_NT')
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
try { Add-Type -AssemblyName System.Net.Http } catch { }
$script:Trust51 = $false
# The .cmd launcher drops a marker file here so it can tell "the script never started" (policy / antivirus) from "it ran and failed".
if ($env:FULLUI_MARK) { try { Set-Content -LiteralPath $env:FULLUI_MARK -Value 'started' } catch { } }

# ---------------------------------------------------------------- logging
function Add-Secret([string]$v) { if ($v -and $v.Length -ge 3 -and -not $script:Secrets.Contains($v)) { [void]$script:Secrets.Add($v) } }
function Remove-Secrets([string]$t) {
    if ($null -eq $t) { return '' }
    foreach ($s in $script:Secrets) { $t = $t.Replace($s, '***') }
    $t = [regex]::Replace($t, '(?i)(Token\s*=\s*")[^"]*', '$1***')
    $t = [regex]::Replace($t, '(?i)((?:api_?key|apikey|x-emby-token|access_?token)\s*[=:]\s*"?)[^&\s",]+', '$1***')
    $t = [regex]::Replace($t, '(?i)("(?:Pw|Password|TmdbApiKey|AccessToken)"\s*:\s*")[^"]*', '$1***')
    $t = [regex]::Replace($t, '(?i)(Bearer\s+)[A-Za-z0-9._\-]+', '$1***')
    return $t
}
function Write-Log([string]$m) {
    if (-not $script:LogFile) { return }
    try { Add-Content -LiteralPath $script:LogFile -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), (Remove-Secrets $m)) -Encoding UTF8 } catch { }
}
function Initialize-Log {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dirs = @($LogDir, (Get-Location).Path, [IO.Path]::GetTempPath()) | Where-Object { $_ }
    foreach ($d in $dirs) {
        try {
            if (-not (Test-Path -LiteralPath $d)) { continue }
            $f = Join-Path $d "FullUI-install-$stamp.log"
            Set-Content -LiteralPath $f -Value "FullUI installer $InstallerVersion  PS $($PSVersionTable.PSVersion)  OS $([Environment]::OSVersion.VersionString)" -Encoding UTF8
            $script:LogFile = $f
            return
        } catch { }
    }
}

# ---------------------------------------------------------------- console output
function Out-Say([string]$m, [string]$color) {
    if ($color) { Write-Host $m -ForegroundColor $color } else { Write-Host $m }
    Write-Log $m
}
function Say([string]$m) { Out-Say $m $null }
function Say-Step([int]$n, [string]$m) { Say ''; Out-Say ("[{0}/{1}] {2}" -f $n, $script:Total, $m) 'Cyan' }
function Say-Ok([string]$m) { Out-Say "      OK   $m" 'Green' }
function Say-Info([string]$m) { Out-Say "      $m" $null }
function Say-Warn([string]$m) { Out-Say "      NOTE $m" 'Yellow' }

# A "friendly" failure: problem + fix. Thrown, caught once at the bottom.
function Fail([string]$problem, [string]$fix, [string]$tech) {
    $e = New-Object System.Exception($problem)
    $e.Data['fix'] = $fix
    if ($tech) { $e.Data['tech'] = $tech }
    throw $e
}
function Cancel-Run([string]$why) {
    $e = New-Object System.Exception($why)
    $e.Data['cancel'] = $true
    throw $e
}

# ---------------------------------------------------------------- prompts
function Read-Line([string]$prompt, [switch]$Secure) {
    try {
        if ($Secure) { return (Read-Host $prompt -AsSecureString) }
        return (Read-Host $prompt)
    } catch {
        Fail 'The installer needs to ask you a question, but this window cannot take typed answers.' 'Start it by double-clicking Install-FullUI.cmd (or run install.sh in a terminal). For scripts, use -Unattended with -Username and -Password.'
    }
}
function Ask([string]$prompt, [string]$default) {
    if ($Unattended) { return $default }
    $suffix = ''
    if ($default) { $suffix = " [$default]" }
    $r = Read-Line ("      " + $prompt + $suffix)
    if ($null -eq $r) { $r = '' }
    $r = $r.Trim()
    if ($r -eq '') { return $default }
    return $r
}
function Ask-YesNo([string]$prompt, [bool]$default) {
    if ($Unattended) { return $default }
    $d = 'y/N'; if ($default) { $d = 'Y/n' }
    while ($true) {
        $r = Read-Line ("      " + $prompt + " ($d)")
        if ($null -eq $r) { $r = '' }
        $r = $r.Trim().ToLower()
        if ($r -eq '') { return $default }
        if ($r -in @('y', 'yes')) { return $true }
        if ($r -in @('n', 'no')) { return $false }
        Say-Info 'Please type y or n.'
    }
}
function Ask-Secret([string]$prompt) {
    if ($Unattended) { return '' }
    if ([Console]::IsInputRedirected) {
        $r = Read-Line ("      " + $prompt)
        if ($null -eq $r) { return '' }
        return $r
    }
    $ss = Read-Line ("      " + $prompt) -Secure
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# ---------------------------------------------------------------- HTTP
function Get-NetErrorKind([string]$msg) {
    $m = "$msg".ToLower()
    if ($m -match 'ssl|tls|certificate|trust relationship|remote certificate') { return 'tls' }
    if ($m -match 'resolved|no such host|name or service|nodename|dns|unknown host') { return 'dns' }
    if ($m -match 'refused|actively refused|connection refused') { return 'refused' }
    if ($m -match 'timed out|timeout|canceled|cancelled|task was canceled') { return 'timeout' }
    if ($m -match 'proxy|407') { return 'proxy' }
    return 'other'
}

function Invoke-Api {
    param(
        [string]$Method = 'GET',
        [string]$Url,
        $Body = $null,
        [string]$Token = $script:Token,
        [int]$TimeoutSec = 30,
        [int]$Retries = 2,
        [hashtable]$Headers = $null,
        [switch]$NoAuthHeader
    )
    $attempt = 0
    $res = $null
    while ($true) {
        $attempt++
        $res = [pscustomobject]@{ Status = 0; Body = ''; Json = $null; Error = ''; Kind = ''; Url = $Url; FinalUrl = $Url }
        $client = $null; $handler = $null; $req = $null; $resp = $null
        try {
            $handler = New-Object System.Net.Http.HttpClientHandler
            # normal proxy settings, except for the Jellyfin server itself (usually on the LAN): contact it directly
            $handler.UseProxy = -not (Test-DirectHost $Url)
            try { $handler.DefaultProxyCredentials = [Net.CredentialCache]::DefaultCredentials } catch { }
            if ($script:Insecure -and $script:InsecureHost -and (Get-HostOf $Url) -eq $script:InsecureHost) {
                # only for the Jellyfin host the user agreed to - never TMDB, GitHub, ...
                try { $handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator } catch { }
                if ($PSVersionTable.PSVersion.Major -lt 6) {
                    # Windows PowerShell 5.1: the callback is process-wide, so it checks the host itself and keeps the normal rules for all others
                    if (-not $script:Trust51) {
                        Add-Type -TypeDefinition 'public static class FullUITrust { public static string Host = ""; public static bool Check(object s, System.Security.Cryptography.X509Certificates.X509Certificate c, System.Security.Cryptography.X509Certificates.X509Chain ch, System.Net.Security.SslPolicyErrors e) { if (e == System.Net.Security.SslPolicyErrors.None) { return true; } System.Net.HttpWebRequest r = s as System.Net.HttpWebRequest; return r != null && Host.Length > 0 && string.Equals(r.RequestUri.Host, Host, System.StringComparison.OrdinalIgnoreCase); } }'
                        [Net.ServicePointManager]::ServerCertificateValidationCallback = [System.Delegate]::CreateDelegate([Net.Security.RemoteCertificateValidationCallback], [FullUITrust].GetMethod('Check'))
                        $script:Trust51 = $true
                    }
                    [FullUITrust]::Host = $script:InsecureHost
                }
            }
            $client = New-Object System.Net.Http.HttpClient($handler)
            $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
            $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($Method)), $Url)
            [void]$req.Headers.TryAddWithoutValidation('Accept', 'application/json')
            [void]$req.Headers.TryAddWithoutValidation('User-Agent', "FullUI-Installer/$InstallerVersion")
            if (-not $NoAuthHeader) {
                $dev = ("$env:COMPUTERNAME" -replace '[^A-Za-z0-9_-]', ''); if (-not $dev) { $dev = 'pc' }
                $auth = 'MediaBrowser Client="FullUI Installer", Device="' + $dev + '", DeviceId="fullui-installer-' + $dev + '", Version="' + $InstallerVersion + '"'
                if ($Token) { $auth += ', Token="' + $Token + '"' }
                [void]$req.Headers.TryAddWithoutValidation('Authorization', $auth)
            }
            if ($Headers) { foreach ($k in $Headers.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, [string]$Headers[$k]) } }
            if ($null -ne $Body) {
                $json = $Body
                if ($Body -isnot [string]) { $json = ConvertTo-Json -InputObject $Body -Depth 32 -Compress }
                $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
            }
            $resp = $client.SendAsync($req).GetAwaiter().GetResult()
            $res.Status = [int]$resp.StatusCode
            try { $res.FinalUrl = $resp.RequestMessage.RequestUri.AbsoluteUri } catch { }
            $res.Body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($res.Body -and ($res.Body.TrimStart().StartsWith('{') -or $res.Body.TrimStart().StartsWith('['))) {
                try { $res.Json = $res.Body | ConvertFrom-Json } catch { }
            }
        } catch {
            $m = $_.Exception.Message
            $inner = $_.Exception
            while ($inner.InnerException) { $inner = $inner.InnerException; $m += ' | ' + $inner.Message }
            $res.Error = $m
            $res.Kind = Get-NetErrorKind $m
        } finally {
            foreach ($o in @($resp, $req, $client, $handler)) { if ($o) { try { $o.Dispose() } catch { } } }
        }
        Write-Log ("HTTP {0} {1} -> {2} {3}" -f $Method, $Url, $res.Status, $res.Error)
        $transient = ($res.Status -eq 0 -and $res.Kind -notin @('tls', 'dns')) -or $res.Status -in @(408, 429, 502, 503, 504)
        if ($transient -and $attempt -le $Retries) {
            $wait = [math]::Pow(2, $attempt - 1)
            Write-Log "transient problem, retry $attempt after ${wait}s"
            Start-Sleep -Seconds $wait
            continue
        }
        return $res
    }
}

# Plain-English explanation of a failed response.
function Explain-Http($r, [string]$what) {
    if ($r.Status -eq 0) {
        switch ($r.Kind) {
            'tls'     { return "$what failed because of a security-certificate problem (HTTPS)." }
            'dns'     { return "$what failed because the address could not be found. Check the spelling and your internet/network." }
            'refused' { return "$what failed because nothing is listening there (connection refused). Is Jellyfin running?" }
            'timeout' { return "$what timed out. The other side did not answer in time." }
            'proxy'   { return "$what was blocked by a proxy server on your network." }
            default   { return "$what failed because of a network problem." }
        }
    }
    switch ($r.Status) {
        401 { return "$what was refused: not signed in or wrong credentials (HTTP 401)." }
        403 { return "$what was refused: this account is not allowed to do that (HTTP 403)." }
        404 { return "$what was not found (HTTP 404). The address may be wrong, or this Jellyfin does not have that feature." }
        { $_ -ge 500 } { return "$what hit an error on the server side (HTTP $($r.Status)). Jellyfin may still be starting, or something went wrong inside it." }
        default { return "$what failed (HTTP $($r.Status))." }
    }
}

function Api([string]$path) { return ($script:Base + $path) }
function Get-HostOf([string]$url) { try { return ([uri]$url).Host.ToLower() } catch { return '' } }
function Test-DirectHost([string]$url) {
    $h = Get-HostOf $url
    if ($h -in @('localhost', '127.0.0.1', '::1', '[::1]')) { return $true }
    if ($script:Base -and $h -and $h -eq (Get-HostOf $script:Base)) { return $true }
    return $false
}

# ---------------------------------------------------------------- helpers
function Norm-Version([string]$v) {
    # '10.11.4-rc1' -> 10.11.4.0. Never throws, never loops: missing or junk parts count as 0 (B-85).
    $nums = New-Object System.Collections.ArrayList
    foreach ($x in ("$v" -replace '[^0-9.].*$', '').Split('.')) {
        if ($nums.Count -ge 4) { break }
        if ($x -notmatch '^[0-9]+$') { continue }
        $n = 0
        if (-not [int]::TryParse($x, [ref]$n)) { $n = 0 }
        [void]$nums.Add($n)
    }
    while ($nums.Count -lt 4) { [void]$nums.Add(0) }
    return (New-Object System.Version($nums[0], $nums[1], $nums[2], $nums[3]))
}
# Runs a program with Continue: Windows PowerShell 5.1 turns ANY stderr text of a native command into a terminating error
# under 'Stop' (ollama prints its progress on stderr). Output goes to the log; returns the exit code.
function Invoke-Native([string]$exe, [string[]]$ArgList) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @ArgList 2>&1 | ForEach-Object { Write-Log "$_" }
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
}
function Test-LocalAddress([string]$base) {
    try {
        $h = ([uri]$base).Host.ToLower()
        if ($h -in @('localhost', '127.0.0.1', '::1', '[::1]')) { return $true }
        if ($h -eq [Net.Dns]::GetHostName().ToLower()) { return $true }
        foreach ($a in [Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName())) { if ($a.ToString() -eq $h) { return $true } }
    } catch { }
    return $false
}
function Get-Prop($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    foreach ($p in $obj.PSObject.Properties) { if ($p.Name -ieq $name) { return $p.Value } }
    return $null
}
function Set-Prop($obj, [string]$name, $value) {
    foreach ($p in $obj.PSObject.Properties) { if ($p.Name -ieq $name) { $p.Value = $value; return } }
    Add-Member -InputObject $obj -MemberType NoteProperty -Name $name -Value $value -Force
}

# ---------------------------------------------------------------- step 1/2: find Jellyfin, check version
function Get-Candidates([string]$input1) {
    $t = $input1.Trim().TrimEnd('/')
    if ($t -match '^https?://') { return @($t) }
    if ($t -match ':\d+$') { return @("http://$t", "https://$t") }
    return @("http://${t}:8096", "http://$t", "https://$t", "https://${t}:8920")
}
function Test-Jellyfin([string]$base) {
    if ($Insecure) { $script:Insecure = $true; $script:InsecureHost = Get-HostOf $base }
    $r = Invoke-Api -Url ($base + '/System/Info/Public') -TimeoutSec 8 -Retries 0 -NoAuthHeader
    $ok = ($r.Status -eq 200 -and $r.Json -and (Get-Prop $r.Json 'Version'))
    # If the probe was redirected (http -> https, other host), use where it ended up: a POST does not survive a 301 (B-79).
    $final = $base
    $suffix = '/System/Info/Public'
    $fu = "$($r.FinalUrl)".Split('?')[0]
    if ($ok -and $fu.EndsWith($suffix) -and $fu -ne ($base + $suffix)) { $final = $fu.Substring(0, $fu.Length - $suffix.Length) }
    return [pscustomobject]@{ Ok = $ok; Resp = $r; Info = $r.Json; Base = $final }
}
function Find-Jellyfin {
    $tries = 0
    $list = @()
    if ($Server) { $list = Get-Candidates $Server } else { $list = @('http://localhost:8096', 'https://localhost:8920') }
    $lastKind = ''
    while ($true) {
        foreach ($c in $list) {
            Say-Info "Trying $c ..."
            $t = Test-Jellyfin $c
            if (-not $t.Ok -and $t.Resp.Kind -eq 'tls' -and -not $script:Insecure) {
                Say-Warn "$c uses a security certificate this computer does not trust (normal for home servers with a self-made certificate)."
                if ($Unattended) {
                    Fail "The security certificate of $c is not trusted by this computer." "If this is your own server and you trust it, run this again with -Insecure (the certificate is then not checked for that server only)."
                }
                if (Ask-YesNo 'Continue anyway? The password will still be encrypted in transit, but the server identity is not checked.' $true) {
                    $script:Insecure = $true
                    $script:InsecureHost = Get-HostOf $c
                    $t = Test-Jellyfin $c
                }
            }
            if ($t.Ok) {
                $script:Base = $t.Base
                if ($script:Insecure) { $script:InsecureHost = Get-HostOf $script:Base }
                if ($t.Base -ne $c) { Say-Info "$c forwards to $($t.Base) - using that address." }
                return $t.Info
            }
            if ($t.Resp.Status -ne 0 -and $t.Resp.Status -ne 404) { $lastKind = "HTTP $($t.Resp.Status)" } elseif ($t.Resp.Kind) { $lastKind = $t.Resp.Kind }
        }
        $tries++
        if ($Unattended -or $tries -ge 3) {
            Fail "I could not find a Jellyfin server$(if ($Server) { " at '$Server'" } else { ' on this computer' })." `
                 "Make sure Jellyfin is running (open it in your browser first). If it is on another computer, run this again with its address, e.g. -Server 192.168.1.20:8096." `
                 "last error kind: $lastKind"
        }
        if ($tries -eq 1 -and -not $Server) { Say-Info 'Jellyfin was not found on this computer at the usual addresses.' }
        Say-Info 'Please type the address you use in your browser to open Jellyfin,'
        Say-Info 'for example 192.168.1.20 or 192.168.1.20:8096 or http://myserver:8096'
        $a = Ask 'Jellyfin address' ''
        if (-not $a) { Cancel-Run 'No address given.' }
        $list = Get-Candidates $a
    }
}

# ---------------------------------------------------------------- step 3: sign in
function Sign-In {
    if ($ApiKey) {
        Add-Secret $ApiKey
        $script:Token = $ApiKey
        $r = Invoke-Api -Url (Api '/System/Configuration') -Retries 1
        if ($r.Status -eq 200) { Say-Ok 'API key accepted (administrator access).'; return }
        if ($r.Status -in @(401, 403)) {
            Fail 'Jellyfin did not accept that API key.' 'Create a new one in Jellyfin: Dashboard > Advanced > API Keys, then run this again.'
        }
        Fail (Explain-Http $r 'Checking the API key') 'Check that Jellyfin is running, then run this file again.' $r.Error
    }
    $user = $Username
    $pw = $Password
    $attempt = 0
    while ($true) {
        $attempt++
        if (-not $user) {
            Say-Info 'I need the username and password of a Jellyfin ADMINISTRATOR account.'
            Say-Info '(The password is only used right now to sign in. It is never saved or written to the log.)'
            $user = Ask 'Admin username' ''
        }
        if (-not $user) { Cancel-Run 'No username given.' }
        if (-not $pw) { $pw = Ask-Secret 'Password (typing is hidden)' }
        Add-Secret $pw
        $body = ConvertTo-Json -InputObject @{ Username = $user; Pw = $pw } -Compress
        $r = Invoke-Api -Method POST -Url (Api '/Users/AuthenticateByName') -Body $body -Retries 1   # must carry the MediaBrowser Client/Device/DeviceId/Version header (no Token), or Jellyfin answers HTTP 500
        $isAdmin = $false
        if ($r.Status -eq 200 -and $r.Json -and (Get-Prop $r.Json 'AccessToken')) {
            $u = Get-Prop $r.Json 'User'
            $pol = Get-Prop $u 'Policy'
            $isAdmin = [bool](Get-Prop $pol 'IsAdministrator')
            $tok = Get-Prop $r.Json 'AccessToken'
            Add-Secret $tok
            if ($isAdmin) {
                $script:Token = $tok
                $script:UsedPassword = $true
                Say-Ok "Signed in as administrator '$user'."
                return
            }
            # not an admin: sign this session out again, then ask for another account
            [void](Invoke-Api -Method POST -Url (Api '/Sessions/Logout') -Token $tok -Retries 0)
            if ($Unattended -or $attempt -ge 3) {
                Fail "'$user' is a normal user, not an administrator." 'Run this again with the admin account (the one you use for Dashboard > Settings).'
            }
            Say-Warn "'$user' is a normal user, not an administrator. Installing plugins needs an admin account."
            $user = ''; $pw = ''
            continue
        }
        if ($r.Status -in @(401, 403, 400)) {
            if ($Unattended -or $attempt -ge 3) {
                Fail 'The username or password was not accepted.' 'Double-check them (the password is case-sensitive) by signing in to Jellyfin in your browser, then run this again.'
            }
            Say-Warn "That username or password was not accepted. Try again ($attempt of 3 used)."
            $user = ''; $pw = ''
            continue
        }
        Fail (Explain-Http $r 'Signing in') 'Check that Jellyfin is running and reachable, then run this file again.' $r.Error
    }
}

# ---------------------------------------------------------------- step 4: repositories
function Test-ManifestUsable($json, [string]$guid, [string]$pluginName) {
    # a repository list only counts if it really offers this plugin with at least one version (B-78)
    foreach ($e in @($json)) {
        if ($null -eq $e) { continue }
        $g = "$(Get-Prop $e 'guid')".Replace('-', '').ToLower()
        $n = "$(Get-Prop $e 'name')".ToLower()
        if ($g -eq $guid.Replace('-', '').ToLower() -or $n -eq $pluginName.ToLower()) {
            $vs = @(Get-Prop $e 'versions' | Where-Object { $null -ne $_ })
            if ($vs.Count -gt 0) { return $true }
        }
    }
    return $false
}
function Test-Manifest([string]$url, [string]$guid, [string]$pluginName) {
    $r = Invoke-Api -Url $url -TimeoutSec 30 -Retries 2 -NoAuthHeader
    $usable = ($r.Status -eq 200 -and $r.Json -and (Test-ManifestUsable $r.Json $guid $pluginName))
    return [pscustomobject]@{ Ok = [bool]$usable; Resp = $r }
}
function Ensure-Repository([string]$name, [string[]]$urls, [string]$guid) {
    $chosen = $null
    foreach ($u in $urls) {
        $t = Test-Manifest $u $guid $name
        if ($t.Ok) { $chosen = $u; break }
        if ($t.Resp.Status -eq 200) {
            Say-Warn "The $name list at $u answered, but it does not offer $name (empty or out of date). Trying the next address if there is one."
        } else {
            Say-Warn ((Explain-Http $t.Resp "Reaching the $name list at $u") + ' Trying the next address if there is one.')
        }
    }
    if (-not $chosen) {
        Fail "This computer cannot download the $name plugin list." "Check your internet connection (and any proxy or firewall), or try again later - the site may be temporarily down." ($urls -join ', ')
    }
    $norm = { param($x) ("$x").Trim().TrimEnd('/').ToLower() }
    # Preferred: Jellyfin's own repositories API (touches nothing else). Servers without it: the settings file.
    $rr = Invoke-Api -Url (Api '/Repositories') -Retries 2
    $useApi = ($rr.Status -eq 200 -and "$($rr.Body)".TrimStart().StartsWith('['))
    $sysCfg = $null
    $repos = @()
    if ($useApi) {
        if ($rr.Json) { $repos = @($rr.Json | Where-Object { $null -ne $_ }) }
    } elseif ($rr.Status -in @(404, 405)) {
        $cfgR = Invoke-Api -Url (Api '/System/Configuration') -Retries 2
        if ($cfgR.Status -ne 200 -or -not $cfgR.Json) {
            Fail (Explain-Http $cfgR 'Reading the Jellyfin settings') 'Make sure you signed in with an administrator account, then run this file again.' $cfgR.Error
        }
        $sysCfg = $cfgR.Json
        $existing = Get-Prop $sysCfg 'PluginRepositories'
        if ($existing) { $repos = @($existing) }
    } else {
        Fail (Explain-Http $rr 'Reading the plugin repositories') 'Make sure you signed in with an administrator account, then run this file again.' $rr.Error
    }
    $found = $null
    foreach ($rp in $repos) { if ((& $norm (Get-Prop $rp 'Url')) -eq (& $norm $chosen)) { $found = $rp } }
    if ($found) {
        if (Get-Prop $found 'Enabled') { Say-Ok "$name repository already present."; return $chosen }
        Set-Prop $found 'Enabled' $true
        Say-Info "$name repository was switched off; turning it on."
    } else {
        $new = [pscustomobject]@{ Name = $name; Url = $chosen; Enabled = $true }
        $repos = @($repos) + @($new)
        Say-Info "Adding the $name repository."
    }
    if ($useApi) {
        $w = Invoke-Api -Method POST -Url (Api '/Repositories') -Body @($repos) -Retries 1
    } else {
        Set-Prop $sysCfg 'PluginRepositories' @($repos)
        $w = Invoke-Api -Method POST -Url (Api '/System/Configuration') -Body $sysCfg -Retries 1
    }
    if ($w.Status -notin @(200, 204)) {
        Fail (Explain-Http $w "Saving the $name repository in Jellyfin") 'You need an administrator account. If this keeps happening, add it by hand: Dashboard > Plugins > Repositories > +.' $w.Error
    }
    Say-Ok "$name repository added."
    return $chosen
}

# ---------------------------------------------------------------- step 5/6: install packages
function Get-Packages {
    $r = Invoke-Api -Url (Api '/Packages') -TimeoutSec 120 -Retries 2
    if ($r.Status -ne 200) {
        Fail (Explain-Http $r 'Asking Jellyfin for the list of plugins') 'Jellyfin itself needs internet access to read plugin lists. Check the server computer is online, then run this again.' $r.Error
    }
    $list = @(); if ($r.Json) { $list = @($r.Json) }
    return ,$list
}
function Get-InstalledPlugins {
    $r = Invoke-Api -Url (Api '/Plugins') -Retries 2
    if ($r.Status -ne 200) { return $null }
    $list = @(); if ($r.Json) { $list = @($r.Json) }
    return ,$list
}
function Find-AllInstalled($plugins, [string]$name, [string]$guid) {
    # Jellyfin keeps superseded old versions in /Plugins, so one plugin can appear several times (B-76)
    $out = @()
    foreach ($p in @($plugins)) {
        if ($null -eq $p) { continue }
        if ((("$(Get-Prop $p 'Name')") -ieq $name) -or ($guid -and ("$(Get-Prop $p 'Id')").Replace('-', '') -ieq $guid.Replace('-', ''))) { $out += $p }
    }
    return $out   # callers wrap the result in @() so zero or one entries stay arrays
}
function Find-Installed($plugins, [string]$name, [string]$guid) {
    # the entry that counts: highest version; for equal versions prefer Active, then Restart
    $best = $null
    foreach ($p in (Find-AllInstalled $plugins $name $guid)) {
        $rank = 0
        switch ("$(Get-Prop $p 'Status')") { 'Active' { $rank = 3 } 'Restart' { $rank = 2 } }
        $v = Norm-Version "$(Get-Prop $p 'Version')"
        if ((-not $best) -or ($v -gt $best.V) -or (($v -eq $best.V) -and ($rank -gt $best.R))) { $best = @{ P = $p; V = $v; R = $rank } }
    }
    if ($best) { return $best.P }
    return $null
}
function Select-PackageVersion($pkg, [version]$srv) {
    $best = $null; $bestSame = $null
    foreach ($v in @(Get-Prop $pkg 'versions')) {
        try { $abi = Norm-Version "$(Get-Prop $v 'targetAbi')" } catch { $abi = [version]'0.0.0.0' }
        try { $ver = Norm-Version "$(Get-Prop $v 'version')" } catch { continue }
        if ($abi -gt $srv) { continue }
        $cand = [pscustomobject]@{ V = $v; Abi = $abi; Ver = $ver }
        $better = { param($a, $b) (-not $b) -or ($a.Abi -gt $b.Abi) -or (($a.Abi -eq $b.Abi) -and ($a.Ver -gt $b.Ver)) }
        if (& $better $cand $best) { $best = $cand }
        if ($abi.Major -eq $srv.Major -and $abi.Minor -eq $srv.Minor) { if (& $better $cand $bestSame) { $bestSame = $cand } }
    }
    if ($bestSame) { return [pscustomobject]@{ Version = $bestSame.V; Exact = $true } }
    if ($best) { return [pscustomobject]@{ Version = $best.V; Exact = $false } }
    return $null
}
function Install-One([string]$display, [string]$guid, [bool]$shared) {
    $pkgs = Get-Packages
    $pkg = $null
    foreach ($p in $pkgs) {
        if ((("$(Get-Prop $p 'name')") -ieq $display) -or ($guid -and ("$(Get-Prop $p 'guid')").Replace('-', '') -ieq $guid.Replace('-', ''))) { $pkg = $p; break }
    }
    if (-not $pkg) {
        Fail "Jellyfin's plugin catalog does not list '$display'." "Jellyfin could not read the repository (the server needs internet access), or the plugin has not been published yet. Check the server is online and run this again in a minute." ("packages seen: " + ($pkgs.Count))
    }
    $sel = Select-PackageVersion $pkg (Norm-Version $script:ServerVersion)
    if (-not $sel) {
        Fail "No version of $display is built for Jellyfin $($script:ServerVersion)." "The plugin author may not have updated it for your Jellyfin yet. Try again later, or update/downgrade Jellyfin to a 10.11.x version."
    }
    $ver = Get-Prop $sel.Version 'version'
    if (-not $sel.Exact) { Say-Warn "$display's newest build targets an older Jellyfin than yours; it may not load." }
    $plugins = Get-InstalledPlugins
    $inst = $null; if ($plugins) { $inst = Find-Installed $plugins $display $guid }
    if ($inst) {
        $iv = Norm-Version "$(Get-Prop $inst 'Version')"
        $st = "$(Get-Prop $inst 'Status')"
        $broken = ($st -in @('NotSupported', 'Malfunctioned'))
        if ($iv -ge (Norm-Version $ver) -and -not $broken) {
            Say-Ok "$display $(Get-Prop $inst 'Version') is already installed and up to date."
            return 'uptodate'
        }
        if ($shared -and -not $broken -and -not $Update) {
            # somebody else's plugin may depend on the copy they have: never swap it silently (B-86)
            Say-Info "A newer $display exists ($ver); you have $(Get-Prop $inst 'Version')."
            if ($Unattended -or -not (Ask-YesNo 'Upgrade it? Other plugins may use it; upgrading restarts Jellyfin.' $false)) {
                Say-Ok "$display $(Get-Prop $inst 'Version') is already installed (kept as it is; run with -Update to upgrade it)."
                Say-Info "Keeping your $display."
                return 'uptodate'
            }
        }
        Say-Info "Updating $display from $(Get-Prop $inst 'Version') to $ver."
    } else {
        Say-Info "Installing $display $ver (Jellyfin downloads it itself; this can take a minute)."
    }
    $repoUrl = Get-Prop $sel.Version 'repositoryUrl'
    $q = 'assemblyGuid=' + [uri]::EscapeDataString("$(Get-Prop $pkg 'guid')") + '&version=' + [uri]::EscapeDataString("$ver")
    if ($repoUrl) { $q += '&repositoryUrl=' + [uri]::EscapeDataString("$repoUrl") }
    $url = Api ('/Packages/Installed/' + [uri]::EscapeDataString($display) + '?' + $q)
    # No automatic retry of this POST: a repeat after a timeout could start a second install; we poll /Plugins instead (B-100).
    $r = Invoke-Api -Method POST -Url $url -TimeoutSec $InstallTimeoutSec -Retries 0
    if ($r.Status -notin @(200, 204)) {
        if ($r.Status -eq 0) {
            Say-Warn 'Jellyfin is taking a long time to answer; checking whether the install went through anyway.'
        } else {
            $hint = 'Make sure the server computer has internet access and enough free disk space, then run this again.'
            if ($r.Status -ge 500) { $hint = 'Jellyfin could not download or unpack the file (a blocked download, full disk, or a file in use by antivirus are common causes). Check Dashboard > Logs in Jellyfin, then run this again.' }
            Fail (Explain-Http $r "Installing $display") $hint $r.Body
        }
    }
    $deadline = (Get-Date).AddSeconds($InstallTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $plugins = Get-InstalledPlugins
        if ($plugins) {
            $inst = Find-Installed $plugins $display $guid
            if ($inst -and (Norm-Version "$(Get-Prop $inst 'Version')") -ge (Norm-Version $ver)) {
                Say-Ok "$display $(Get-Prop $inst 'Version') installed (it becomes active after the restart)."
                return 'installed'
            }
        }
        Start-Sleep -Seconds $PollSec
    }
    Fail "$display did not show up as installed within $InstallTimeoutSec seconds." 'Look at Dashboard > Logs in Jellyfin for download errors, then run this again - it will continue where it stopped.'
}

# ---------------------------------------------------------------- step 7: restart
function Test-RestartPending {
    # true while any plugin still waits for a restart (or removal) - proof that Jellyfin has not restarted yet
    $pl = Get-InstalledPlugins
    foreach ($p in @($pl)) { if ($p -and ("$(Get-Prop $p 'Status')" -in @('Restart', 'Deleted'))) { return $true } }
    return $false
}
function Restart-AndWait {
    Say-Info 'Restarting Jellyfin now. The website will be offline for a short moment.'
    $r = Invoke-Api -Method POST -Url (Api '/System/Restart') -Retries 0
    if ($r.Status -notin @(200, 204) -and $r.Status -ne 0) {
        Fail (Explain-Http $r 'Asking Jellyfin to restart') 'Restart Jellyfin by hand (Dashboard > Restart, or restart the Jellyfin service / tray app), then run this file again.' $r.Body
    }
    $start = Get-Date
    $deadline = $start.AddSeconds($RestartTimeoutSec)
    $sawDown = $false
    $lastDot = Get-Date
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $PollSec
        $t = Test-Jellyfin $script:Base
        $elapsed = [int]((Get-Date) - $start).TotalSeconds
        if (-not $t.Ok) { $sawDown = $true }
        elseif ($sawDown -or $elapsed -ge 20) {
            # up again; also wait until the authenticated API answers. "Still answering" only counts as restarted
            # if the server went offline once, or no plugin is waiting for a restart any more (B-97).
            $p = Invoke-Api -Url (Api '/Plugins') -Retries 0
            if ($p.Status -eq 200 -and ($sawDown -or -not (Test-RestartPending))) { Say-Ok "Jellyfin is back after about $elapsed seconds."; return }
        }
        if (((Get-Date) - $lastDot).TotalSeconds -ge 10) { Say-Info "...still waiting ($elapsed s)"; $lastDot = Get-Date }
    }
    if (-not $sawDown) {
        Fail 'Jellyfin never went offline after the restart request, so it does not look like it restarted.' 'Restart Jellyfin by hand (Dashboard > Restart, or restart the Jellyfin service / tray app), then run this file again - nothing is lost.'
    }
    Fail "Jellyfin did not come back within $RestartTimeoutSec seconds." `
         "Start Jellyfin by hand (Windows: open the Jellyfin tray app or the 'Jellyfin Server' service; Linux: 'sudo systemctl restart jellyfin'). Once it is running, run this file again - nothing is lost."
}

# ---------------------------------------------------------------- step 8: verify
function Verify-Active {
    $deadline = (Get-Date).AddSeconds(90)
    $want = @(@{ N = 'File Transformation'; G = $FtGuid }, @{ N = 'FullUI'; G = $FullUIGuid })
    $last = ''
    while ($true) {
        $plugins = Get-InstalledPlugins
        $bad = @()
        $okAll = $true
        foreach ($w in $want) {
            $p = $null; if ($plugins) { $p = Find-Installed $plugins $w.N $w.G }
            $st = ''; if ($p) { $st = "$(Get-Prop $p 'Status')" }
            if ($st -ne 'Active') { $okAll = $false; $bad += "$($w.N)=$(if ($st) { $st } else { 'missing' })" }
        }
        if ($okAll) { Say-Ok 'File Transformation and FullUI are both Active.'; return }
        $last = $bad -join ', '
        if ($last -match 'Malfunctioned|NotSupported|Disabled' -and $last -notmatch 'Restart|missing') { break }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Seconds $PollSec
    }
    $fix = 'Open Jellyfin > Dashboard > Logs and look for lines mentioning the plugin; then run this file again.'
    if ($last -match 'Restart') { $fix = 'Jellyfin has not restarted yet. Restart it by hand (Dashboard > Restart), then run this file again.' }
    if ($last -match 'Malfunctioned') { $fix = 'The plugin crashed while loading, usually because it was built for a different Jellyfin version. Check Dashboard > Logs, and see docs/INSTALL.md troubleshooting.' }
    if ($last -match 'NotSupported') { $fix = 'This plugin does not support your Jellyfin version. See docs/INSTALL.md troubleshooting.' }
    Fail "A plugin did not become active ($last)." $fix
}

# ---------------------------------------------------------------- step 9: configuration
function Get-PluginConfig {
    $r = Invoke-Api -Url (Api "/Plugins/$FullUIGuid/Configuration") -Retries 2
    if ($r.Status -ne 200 -or -not $r.Json) { return $null }
    return $r.Json
}
function Save-PluginConfig($cfg) {
    $r = Invoke-Api -Method POST -Url (Api "/Plugins/$FullUIGuid/Configuration") -Body $cfg -Retries 1
    if ($r.Status -notin @(200, 204)) {
        Fail (Explain-Http $r 'Saving the FullUI settings') 'You can set these later in Jellyfin: Dashboard > Plugins > FullUI.' $r.Body
    }
}
function Test-Tmdb([string]$key) {
    $key = $key.Trim()
    if ($key.StartsWith('eyJ') -or $key.Length -gt 40) {
        return Invoke-Api -Url ($TmdbBaseUrl + '/configuration') -Headers @{ Authorization = "Bearer $key" } -NoAuthHeader -Retries 2
    }
    return Invoke-Api -Url ($TmdbBaseUrl + '/configuration?api_key=' + [uri]::EscapeDataString($key)) -NoAuthHeader -Retries 2
}
function Configure-Tmdb($cfg) {
    $existing = "$(Get-Prop $cfg 'TmdbApiKey')"
    $key = $TmdbKey
    if (-not $key -and $existing -and -not $Unattended) {
        Say-Ok 'A TMDB key is already saved. Keeping it.'
        return $false
    }
    if (-not $key -and $Unattended) { return $false }
    if (-not $key) {
        Say-Info ''
        Say-Info 'OPTIONAL: movie info for "Coming Soon" and trailers comes from TMDB (themoviedb.org).'
        Say-Info 'It needs a free key: make a free account at themoviedb.org, open Settings > API,'
        Say-Info 'and copy the "API Key" (a 32-letter/number code) or the long "Read Access Token".'
        Say-Info 'Press Enter to skip - you can add it later in Dashboard > Plugins > FullUI.'
    }
    $tries = 0
    while ($true) {
        $tries++
        if (-not $key) { $key = Ask-Secret 'TMDB key (hidden; Enter to skip)' }
        if (-not $key) { Say-Info 'Skipping TMDB for now.'; return $false }
        Add-Secret $key
        $t = Test-Tmdb $key
        if ($t.Status -eq 200) {
            Set-Prop $cfg 'TmdbApiKey' $key.Trim()
            Say-Ok 'TMDB accepted the key.'
            return $true
        }
        if ($t.Status -eq 401 -or $t.Status -eq 403) {
            if ($Unattended -or $tries -ge 3) { Say-Warn 'TMDB rejected the key, so I did not save it. Add it later in the plugin settings.'; return $false }
            Say-Warn 'TMDB said that key is not valid. Please copy it again (no spaces).'
            $key = ''
            continue
        }
        Say-Warn ((Explain-Http $t 'Checking the key with TMDB') + ' I cannot verify the key right now.')
        if (-not $Unattended -and (Ask-YesNo 'Save it anyway (you can test it later in the plugin settings)?' $false)) {
            Set-Prop $cfg 'TmdbApiKey' $key.Trim(); return $true
        }
        return $false
    }
}

function Find-Ollama {
    $c = Get-Command ollama -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    if ($IsWin -and $env:LOCALAPPDATA) {
        $p = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}
function Setup-Ollama($cfg) {
    if (-not (Test-LocalAddress $script:Base)) {
        Say-Warn 'Ollama has to be installed on the same computer as Jellyfin. Run this installer on that computer to add it. Skipping.'
        return $false
    }
    $exe = Find-Ollama
    if ($exe) { Say-Ok 'Ollama is already installed.' }
    else {
        Say-Info 'Installing Ollama (this can take a few minutes)...'
        Say-Info 'NOTE: Ollama is software from a third party (ollama.com), not from FullUI. This downloads and runs'
        Say-Info "Ollama's own installer on this computer."
        if ($IsWin) {
            $wg = Get-Command winget -ErrorAction SilentlyContinue
            $done = $false
            if ($wg) {
                $code = Invoke-Native $wg.Source @('install', '--id', 'Ollama.Ollama', '-e', '--silent', '--accept-package-agreements', '--accept-source-agreements')
                if ($code -eq 0) { $done = $true } else { Say-Info 'winget did not finish; trying the direct download.' }
            }
            if (-not $done) {
                $tmp = Join-Path ([IO.Path]::GetTempPath()) ('OllamaSetup-' + [guid]::NewGuid().ToString('N') + '.exe')
                try {
                    try { Invoke-WebRequest -UseBasicParsing -Uri 'https://ollama.com/download/OllamaSetup.exe' -OutFile $tmp -TimeoutSec 600 } catch { Say-Warn 'Could not download the Ollama installer.'; return $false }
                    # the file must carry a valid digital signature, otherwise it is not run
                    $sig = Get-AuthenticodeSignature -LiteralPath $tmp
                    if ($sig.Status -ne 'Valid') {
                        Say-Warn "The downloaded Ollama installer does not have a valid digital signature ($($sig.Status)), so I did not run it. Install Ollama yourself from ollama.com."
                        return $false
                    }
                    $who = ''; if ($sig.SignerCertificate) { $who = $sig.SignerCertificate.Subject }
                    Say-Info "Ollama installer signature is valid. Signed by: $who"
                    $p = Start-Process -FilePath $tmp -ArgumentList '/SILENT' -Wait -PassThru
                    if ($p.ExitCode -ne 0) { Say-Warn "The Ollama installer reported code $($p.ExitCode)."; return $false }
                } finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } }
            }
        } elseif ($PSVersionTable.Platform -eq 'Unix' -and $IsLinux) {
            Say-Info "On Linux that is the script https://ollama.com/install.sh (it may need sudo)."
            $code = Invoke-Native 'sh' @('-c', 'curl -fsSL https://ollama.com/install.sh | sh')
            if ($code -ne 0) { Say-Warn 'The Ollama install script failed (it usually needs sudo/root). You can install it yourself from ollama.com.'; return $false }
        } else {
            Say-Warn 'Please install Ollama from https://ollama.com/download, then run this installer again.'
            return $false
        }
        $exe = Find-Ollama
        if (-not $exe) { Say-Warn 'Ollama was installed but I cannot find it yet. Open a NEW window and run this installer again.'; return $false }
        Say-Ok 'Ollama installed.'
    }
    $up = $false
    for ($i = 0; $i -lt 20 -and -not $up; $i++) {
        $v = Invoke-Api -Url 'http://localhost:11434/api/version' -TimeoutSec 3 -Retries 0 -NoAuthHeader
        if ($v.Status -eq 200) { $up = $true } else {
            if ($i -eq 1) { try { Start-Process -FilePath $exe -ArgumentList 'serve' -WindowStyle Hidden | Out-Null } catch { } }
            Start-Sleep -Seconds 3
        }
    }
    if (-not $up) { Say-Warn 'Ollama is not answering on port 11434. Start it and run this installer again.'; return $false }
    foreach ($m in @('nomic-embed-text', 'llama3.2:3b')) {
        Say-Info "Downloading AI model $m (a few hundred MB to 2 GB; please wait)..."
        $okm = $false
        for ($a = 1; $a -le 2 -and -not $okm; $a++) {
            if ((Invoke-Native $exe @('pull', $m)) -eq 0) { $okm = $true }
        }
        if (-not $okm) { Say-Warn "Could not download $m. Try 'ollama pull $m' yourself later."; return $false }
        Say-Ok "Model $m ready."
    }
    Set-Prop $cfg 'OllamaEnabled' $true
    Set-Prop $cfg 'OllamaUrl' 'http://localhost:11434'
    Set-Prop $cfg 'OllamaEmbedModel' 'nomic-embed-text'
    Set-Prop $cfg 'OllamaChatModel' 'llama3.2:3b'
    return $true
}

function Configure-Plugin {
    $cfg = Get-PluginConfig
    if (-not $cfg) {
        Say-Warn 'Could not read the FullUI settings; skipping optional setup. Use Dashboard > Plugins > FullUI later.'
        return
    }
    $changed = $false
    $curName = "$(Get-Prop $cfg 'ServerName')"
    if ($ServerName) { Set-Prop $cfg 'ServerName' $ServerName; $changed = $true }
    elseif (-not $Unattended -and -not $Update) {
        $n = Ask 'What should your site be called? (the name shown at the top)' $(if ($curName) { $curName } else { 'FullUI' })
        if ($n -and $n -ne $curName) { Set-Prop $cfg 'ServerName' $n; $changed = $true }
    }
    try { if (Configure-Tmdb $cfg) { $changed = $true } } catch { if ($_.Exception.Data['fix']) { throw } else { Say-Warn 'TMDB setup skipped due to an error (see log).'; Write-Log "tmdb: $($_.Exception.Message)" } }
    $wantOllama = $false
    if (-not $SkipOllama) {
        if ($InstallOllama) { $wantOllama = $true }
        elseif (-not $Unattended -and -not $Update -and -not (Get-Prop $cfg 'OllamaEnabled')) {
            Say-Info ''
            Say-Info 'OPTIONAL: smarter search ("find me a funny space movie") uses a small AI that runs on this'
            Say-Info 'computer (Ollama). It is a free download of a few GB and works fine on a normal CPU.'
            $wantOllama = Ask-YesNo 'Install Ollama and the two small AI models now?' $false
        }
    }
    if ($wantOllama) {
        try { if (Setup-Ollama $cfg) { $changed = $true } } catch { Say-Warn 'Ollama setup did not complete (see log). The rest works without it.'; Write-Log "ollama: $($_.Exception.Message)" }
    }
    if ($changed) { Save-PluginConfig $cfg; Say-Ok 'FullUI settings saved.' } else { Say-Info 'No settings needed changing.' }
}

function Get-ScriptUrlFromPage([string]$html, [string]$pageUrl) {
    # the URL a browser would request for the FullUI script tag in index.html (base path included); $null if there is no tag
    $m = [regex]::Match($html, '(?i)<script[^>]*\ssrc=["'']([^"'']*FullUI/web/fullui\.js[^"'']*)["'']')
    if (-not $m.Success) { return $null }
    try { return (New-Object System.Uri((New-Object System.Uri($pageUrl)), $m.Groups[1].Value)).AbsoluteUri } catch { return $m.Groups[1].Value }
}
function Self-Test {
    $good = $true
    $s = Invoke-Api -Url (Api '/FullUI/Status') -Retries 3
    if ($s.Status -eq 200 -and $s.Json) { Say-Ok "FullUI answers (site name: '$(Get-Prop $s.Json 'serverName')')." }
    else { $good = $false; Say-Warn ((Explain-Http $s 'The FullUI status check') + ' The plugin may need another moment; reload in a minute.') }
    $js = Invoke-Api -Url (Api '/FullUI/web/fullui.js') -Retries 1 -NoAuthHeader
    if ($js.Status -eq 200) { Say-Ok 'The FullUI web bundle is being served.' }
    else { $good = $false; Say-Warn 'The FullUI web bundle was not served (the plugin build may be missing its web files).' }
    $ix = Invoke-Api -Url (Api '/web/index.html') -Retries 1 -NoAuthHeader
    $tag = $null
    if ($ix.Status -eq 200) { $tag = Get-ScriptUrlFromPage $ix.Body $ix.FinalUrl }
    if ($tag) {
        Say-Ok 'Jellyfin web pages are loading the FullUI bundle (File Transformation is working).'
        # fetch the script exactly as the browser would: from the address written in the page (base path included)
        $fetched = Invoke-Api -Url $tag -Retries 1 -NoAuthHeader
        if ($fetched.Status -eq 200) { Say-Ok "The script tag points to a file that is served ($tag)." }
        else {
            $good = $false
            Say-Warn "The page asks the browser for $tag, but the server answers HTTP $($fetched.Status) there, so the browser will not load FullUI."
            Say-Info "This usually means Jellyfin runs under a base path (a 'Base URL' setting) and the plugin ignores it. See docs/INSTALL.md."
            Say-Info 'The page does not load the FullUI script correctly yet.'
        }
    } else {
        $good = $false
        Say-Warn 'The Jellyfin web page does NOT load FullUI yet.'
        Say-Info 'Most likely File Transformation has not applied. Restart Jellyfin once more, wait a minute,'
        Say-Info 'then run this file again. See docs/INSTALL.md, "The site looks like normal Jellyfin".'
    }
    return $good
}

function Test-Ipv4([string]$ip) {
    if ($ip.Trim() -notmatch '^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$') { return $false }
    foreach ($i in 1..4) { if ([int]$Matches[$i] -gt 255) { return $false } }
    return $true
}
function Show-FireTv {
    Say ''
    Say 'Fire TV / Fire Stick app'
    $h = Invoke-Api -Method HEAD -Url $ApkUrl -TimeoutSec 15 -Retries 1 -NoAuthHeader
    $have = $h.Status -eq 200
    if ($h.Status -eq 404) {
        Say-Info 'The Fire TV app has not been published yet. It is coming; run this installer with -Update later'
        Say-Info 'or check https://github.com/Narsmow/FullUISuit/releases/tag/firetv-latest for "FullUI-release.apk".'
        return
    }
    Say-Info '1. On the Fire TV, install the free "Downloader" app from the Amazon Appstore.'
    Say-Info '2. Settings > My Fire TV > Developer Options > Install unknown apps > allow Downloader.'
    Say-Info '3. Open Downloader and type exactly:'
    Say-Info "      $ApkUrl"
    Say-Info '4. Choose Install. Then sign in with your Jellyfin address and username.'
    if (-not $have) { Say-Info '(I could not confirm the file is online right now; if the download fails, it is not published yet.)' }
    $adb = Get-Command adb -ErrorAction SilentlyContinue
    if ($adb -and -not $Unattended -or ($adb -and $FireTvIp)) {
        $ip = $FireTvIp
        if (-not $ip) { $ip = Ask 'adb found. To install on the Fire TV right now, type its IP address (Fire TV: Settings > My Fire TV > About > Network). Enter to skip' '' }
        if ($ip -and -not (Test-Ipv4 $ip)) {
            Say-Warn "'$ip' does not look like an IPv4 address (like 192.168.1.30), so I skipped the adb install."
            $ip = ''
        }
        if ($ip) {
            $tmp = Join-Path ([IO.Path]::GetTempPath()) ('FullUI-FireTV-' + [guid]::NewGuid().ToString('N') + '.apk')
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $ApkUrl -OutFile $tmp -TimeoutSec 600
                [void](Invoke-Native $adb.Source @('connect', "${ip}:5555"))
                $code = Invoke-Native $adb.Source @('-s', "${ip}:5555", 'install', '-r', $tmp)
                if ($code -eq 0) { Say-Ok 'Installed on the Fire TV.' } else { Say-Warn 'adb could not install it (is ADB debugging on? Allow the connection on the TV screen). Use the Downloader steps above.' }
            } catch { Say-Warn 'Could not install over adb. Use the Downloader steps above.'; Write-Log "adb: $($_.Exception.Message)" }
            finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } }
        }
    }
}

function Show-Summary([bool]$selfOk) {
    Say ''
    Say '=============================================================='
    if ($selfOk) { Out-Say '  All done! FullUI is installed.' 'Green' } else { Out-Say '  FullUI is installed, but one check needs attention (see above).' 'Yellow' }
    Say '=============================================================='
    Say "  Open your site:   $($script:Base)/web/   (press Ctrl+F5 once to refresh)"
    Say '  Log in:           with your normal Jellyfin username and password.'
    Say '  Settings:         Dashboard > Plugins > FullUI (name, TMDB key, AI search).'
    Say '  Update later:     run this same file again.'
    Say '  Uninstall:        run it with -Uninstall (see docs/INSTALL.md).'
    if ($script:LogFile) { Say "  Technical log:    $($script:LogFile)" }
}

# ---------------------------------------------------------------- main flows
function Run-Install {
    $script:Total = 9
    Say-Step 1 'Looking for your Jellyfin server...'
    $info = Find-Jellyfin
    Say-Ok "Found '$(Get-Prop $info 'ServerName')' at $($script:Base)"

    Say-Step 2 'Checking the Jellyfin version...'
    $script:ServerVersion = "$(Get-Prop $info 'Version')"
    $sv = Norm-Version $script:ServerVersion
    if ($sv.Major -eq 10 -and $sv.Minor -eq 11) { Say-Ok "Version $($script:ServerVersion) - supported." }
    else {
        Say-Warn "Your Jellyfin is version $($script:ServerVersion). FullUI is built for 10.11.x only."
        Say-Info 'On other versions the plugin may refuse to load or show a broken page.'
        Say-Info 'Best fix: update Jellyfin to 10.11.x (jellyfin.org/downloads) and run this again.'
        if (-not $AllowOtherVersion) {
            if ($Unattended) { Fail "Jellyfin $($script:ServerVersion) is not 10.11.x." 'Update Jellyfin to 10.11.x, or pass -AllowOtherVersion to try anyway.' }
            if (-not (Ask-YesNo 'Continue anyway at your own risk?' $false)) { Cancel-Run 'Stopped because of the Jellyfin version. Nothing was changed.' }
        } else { Say-Info '-AllowOtherVersion given; continuing.' }
    }

    Say-Step 3 'Signing in as a Jellyfin administrator...'
    Sign-In

    Say-Step 4 'Adding the plugin download sources (repositories)...'
    Say-Info 'NOTE: File Transformation is made by a third party (IAmParadox27) and published at www.iamparadox.dev;'
    Say-Info "its download list is added to Jellyfin permanently (Dashboard > Plugins > Repositories shows it; you can remove it there)."
    Say-Info "Jellyfin checks its downloads with MD5 checksums only. FullUI itself comes from this project's GitHub."
    [void](Ensure-Repository 'File Transformation' @($FileTransformationRepoUrl) $FtGuid)
    $fullUrls = @($FullUIRepoUrl)
    if ($FullUIRepoFallbackUrl -and $FullUIRepoFallbackUrl -ne $FullUIRepoUrl) { $fullUrls += $FullUIRepoFallbackUrl }
    [void](Ensure-Repository 'FullUI' $fullUrls $FullUIGuid)

    Say-Step 5 'Installing File Transformation (lets plugins change the web page)...'
    $a = Install-One 'File Transformation' $FtGuid $true

    Say-Step 6 'Installing FullUI...'
    $b = Install-One 'FullUI' $FullUIGuid $false
    if ($Update -and $a -eq 'uptodate' -and $b -eq 'uptodate') {
        Say ''
        Say-Ok 'Everything is already on the newest version. Nothing to update.'
        $script:Done = $true
        return
    }

    Say-Step 7 'Restarting Jellyfin so the plugins load...'
    $plugins = Get-InstalledPlugins
    $pending = $false
    foreach ($w in @(@('File Transformation', $FtGuid), @('FullUI', $FullUIGuid))) {
        $p = $null; if ($plugins) { $p = Find-Installed $plugins $w[0] $w[1] }
        if ($p -and "$(Get-Prop $p 'Status')" -eq 'Restart') { $pending = $true }
    }
    if ($a -eq 'uptodate' -and $b -eq 'uptodate' -and -not $pending) { Say-Ok 'Nothing new was installed, so no restart is needed.' }
    else {
        if (-not $Unattended -and -not (Ask-YesNo 'Jellyfin will restart now (anyone watching will be interrupted for ~1 minute). Continue?' $true)) {
            Cancel-Run 'You chose not to restart. The plugins are downloaded and will start the next time Jellyfin restarts; run this file again then.'
        }
        Restart-AndWait
    }

    Say-Step 8 'Checking that both plugins are active...'
    Verify-Active

    Say-Step 9 'Final setup and self-test...'
    Configure-Plugin
    $ok = Self-Test
    Show-FireTv
    Show-Summary $ok
    $script:Done = $true
}

function Remove-AllVersions($entries, [string]$label, [string]$fix) {
    # delete every installed version of a plugin (Jellyfin keeps superseded ones); an entry that is already gone is fine
    foreach ($e in @($entries)) {
        $r = Invoke-Api -Method DELETE -Url (Api "/Plugins/$(Get-Prop $e 'Id')/$(Get-Prop $e 'Version')") -Retries 1
        if ($r.Status -notin @(200, 204, 404)) { Fail (Explain-Http $r "Removing $label $(Get-Prop $e 'Version')") $fix $r.Body }
    }
}
function Run-Uninstall {
    $script:Total = 6
    Say-Step 1 'Looking for your Jellyfin server...'
    $info = Find-Jellyfin
    Say-Ok "Found '$(Get-Prop $info 'ServerName')' at $($script:Base)"
    $script:ServerVersion = "$(Get-Prop $info 'Version')"
    Say-Step 2 'Signing in as a Jellyfin administrator...'
    Sign-In
    Say-Step 3 'Looking at installed plugins...'
    $plugins = Get-InstalledPlugins
    if ($null -eq $plugins) { Fail 'I could not read the list of installed plugins.' 'Check that you used an administrator account, then run this again.' }
    $fu = @(Find-AllInstalled $plugins 'FullUI' $FullUIGuid)
    $ft = @(Find-AllInstalled $plugins 'File Transformation' $FtGuid)
    if ($fu.Count -eq 0) { Say-Ok 'FullUI is not installed. Nothing to remove.' } else { Say-Info ("FullUI " + ((@($fu | ForEach-Object { Get-Prop $_ 'Version' })) -join ', ') + " is installed.") }
    if (-not $Unattended -and $fu.Count -gt 0) {
        if (-not (Ask-YesNo 'Really remove FullUI? (Your settings and data are kept so a later reinstall picks up where you left off.)' $false)) { Cancel-Run 'Cancelled. Nothing was removed.' }
    }
    $removed = $false
    Say-Step 4 'Removing FullUI...'
    if ($fu.Count -gt 0) {
        Remove-AllVersions $fu 'FullUI' 'Use an administrator account, or remove it in Dashboard > Plugins > FullUI > Uninstall.'
        Say-Ok 'FullUI removed.'
        $removed = $true
    } else { Say-Info 'Skipped.' }
    Say-Step 5 'File Transformation (other plugins may use it)...'
    $rmFt = [bool]$RemoveFileTransformation
    if ($ft.Count -gt 0 -and -not $rmFt -and -not $Unattended) { $rmFt = Ask-YesNo 'Also remove File Transformation? Choose No if you use other plugins like Home Screen Sections.' $false }
    if ($ft.Count -gt 0 -and $rmFt) {
        Remove-AllVersions $ft 'File Transformation' 'Remove it in Dashboard > Plugins instead.'
        Say-Ok 'File Transformation removed.'; $removed = $true
    } else { Say-Info 'Kept.' }
    Say-Step 6 'Finishing...'
    if ($removed) {
        $go = $Unattended -or (Ask-YesNo 'Restart Jellyfin now so the removal takes effect?' $true)
        if ($go) { Restart-AndWait } else { Say-Info 'Restart Jellyfin yourself later (Dashboard > Restart) to finish.' }
    }
    if ($PurgeData) {
        Say-Info 'Note: Jellyfin cannot delete plugin data through its web API. To erase it, stop Jellyfin and delete the'
        Say-Info 'FullUI files: <data folder>/fullui/store.json and plugins/configurations/Jellyfin.Plugin.FullUI.xml (the plugin folder itself is removed by the uninstall).'
    } else { Say-Info 'Your FullUI settings and data were kept.' }
    Say ''
    Out-Say '  Done. FullUI has been uninstalled.' 'Green'
    $script:Done = $true
}

# ---------------------------------------------------------------- entry
Initialize-Log
Say ''
Out-Say "FullUI installer $InstallerVersion" 'White'
Say 'This will set up FullUI on your Jellyfin server. It is safe to run again at any time.'
if ($script:LogFile) { Write-Log 'log started' }
$exitCode = 1
try {
    if ($Uninstall) { Run-Uninstall } else { Run-Install }
    $exitCode = 0
} catch {
    $ex = $_.Exception
    $script:Reported = $true
    if ($ex.Data['cancel']) {
        Say ''
        Out-Say ("STOPPED: " + $ex.Message) 'Yellow'
        Say '  Nothing is half-installed. You can run this file again whenever you like.'
        $exitCode = 2
    } elseif ($ex -is [System.Management.Automation.PipelineStoppedException] -or $ex -is [OperationCanceledException]) {
        Say ''
        Out-Say 'STOPPED: you cancelled the installer.' 'Yellow'
        Say '  It is safe to run the file again; it picks up where it stopped.'
        $exitCode = 2
    } else {
        Say ''
        Out-Say 'SOMETHING WENT WRONG' 'Red'
        if ($ex.Data['fix']) {
            Out-Say ('  What happened: ' + $ex.Message) 'Red'
            Say ('  What to try:   ' + $ex.Data['fix'])
        } else {
            Out-Say '  What happened: an unexpected problem occurred inside the installer.' 'Red'
            Say '  What to try:   run this file again; if it repeats, send the log file (below) to whoever set this up.'
        }
        Say '  It is safe to run this file again - it never duplicates anything.'
        Write-Log ("ERROR: " + $ex.Message)
        if ($ex.Data['tech']) { Write-Log ("detail: " + $ex.Data['tech']) }
        Write-Log ("at: " + $_.ScriptStackTrace)
        if ($script:LogFile) { Say "  Technical log:   $($script:LogFile)" }
    }
} finally {
    if ($script:UsedPassword -and $script:Token -and $script:Base) {
        try { [void](Invoke-Api -Method POST -Url (Api '/Sessions/Logout') -Retries 0 -TimeoutSec 5) } catch { }
    }
    if (-not $script:Done -and -not $script:Reported) {
        Say ''
        Out-Say 'STOPPED: the installer was interrupted. It is safe to run the file again.' 'Yellow'
        exit 2
    }
}
exit $exitCode
