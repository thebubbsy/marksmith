<#
.SYNOPSIS
    Dell Asset Warranty & Hardware Refresh Lifecycle Inspection Engine (Dell eAPI v5)
.DESCRIPTION
    Directly queries Dell Technologies Enterprise Warranty API (SBIL eAPI v5) to retrieve
    complete asset lifecycle details, active service contracts, initial & extended entitlements,
    and computes an automated enterprise hardware refresh determination.

    Engineered for IT Fleet Refreshes, OOBE Technician Validation, and Asset Management.

    Credentials can be supplied via:
    1. Direct parameters (-ClientId, -ClientSecret)
    2. Environment variables ($env:DELL_CLIENT_ID, $env:DELL_CLIENT_SECRET)
    3. .env configuration file
    4. Embedded default fallback credentials
.EXAMPLE
    .\Get-DellWarranty.ps1
    Inspects the local machine's BIOS Service Tag.
.EXAMPLE
    .\Get-DellWarranty.ps1 -ServiceTag "6BYQJW2" -OutputFormat Table
.EXAMPLE
    Get-DellWarranty -ServiceTag "6BYQJW2" -ExportCsv ".\WarrantyAudit.csv"
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true)]
    [string]$ServiceTag = '',

    [string]$ClientId = '',
    [string]$ClientSecret = '',
    [string]$EnvFile = '',

    [ValidateSet('Object', 'Json', 'Markdown', 'Table')]
    [string]$OutputFormat = 'Table',

    [string]$ExportCsv = ''
)

# 1. Enforce Modern TLS
try {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13
} catch {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
}

# 2. Helper: Load .env File
function Import-EnvConfigFile {
    param([string]$Path)
    if (-not $Path) {
        $candidates = @(
            (Join-Path $PSScriptRoot '.env'),
            '.\.env',
            (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.env')
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) {
                $Path = $c
                break
            }
        }
    }
    if ($Path -and (Test-Path $Path)) {
        Get-Content $Path | Where-Object { $_ -match '^\s*[^#=]+\s*=' } | ForEach-Object {
            $key, $val = $_ -split '=', 2
            $k = $key.Trim()
            $v = $val.Trim().Trim('"').Trim("'")
            if (-not [string]::IsNullOrWhiteSpace($k)) {
                [Environment]::SetEnvironmentVariable($k, $v, 'Process')
            }
        }
    }
}

Import-EnvConfigFile -Path $EnvFile

# 3. Resolve API Credentials
$effClientId = if ($ClientId) { $ClientId } elseif ($env:DELL_CLIENT_ID) { $env:DELL_CLIENT_ID } else { 'l71df1d39771064ce8a49569b4b56b67c5' }
$effClientSecret = if ($ClientSecret) { $ClientSecret } elseif ($env:DELL_CLIENT_SECRET) { $env:DELL_CLIENT_SECRET } else { 'c4ec5f7556fa4bc6bb1a5164878f5e2c' }
$tokenUrl = if ($env:DELL_TOKEN_URL) { $env:DELL_TOKEN_URL } else { 'https://apigtwb2c.us.dell.com/auth/oauth/v2/token' }
$warrantyUrl = if ($env:DELL_WARRANTY_URL) { $env:DELL_WARRANTY_URL } else { 'https://apigtwb2c.us.dell.com/PROD/sbil/eapi/v5/asset-entitlements' }

# 4. Resolve Target Service Tag
$targetTag = $ServiceTag.Trim()
if ([string]::IsNullOrWhiteSpace($targetTag)) {
    try {
        $targetTag = (Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop).SerialNumber.Trim()
    } catch {
        $targetTag = (Get-WmiObject -Class Win32_BIOS -ErrorAction SilentlyContinue).SerialNumber.Trim()
    }
}

if ([string]::IsNullOrWhiteSpace($targetTag)) {
    throw "Unable to detect host BIOS Service Tag. Please pass -ServiceTag explicitly."
}

# 5. Acquire Bearer Token
Write-Verbose "Acquiring Dell OAuth 2.0 Bearer Token from $tokenUrl..."
$tokenHeaders = @{ 'Content-Type' = 'application/x-www-form-urlencoded' }
$tokenBody = @{
    client_id     = $effClientId
    client_secret = $effClientSecret
    grant_type    = 'client_credentials'
}

$tokenResponse = $null
try {
    $tokenResponse = Invoke-RestMethod -Uri $tokenUrl -Method Post -Headers $tokenHeaders -Body $tokenBody -ErrorAction Stop
} catch {
    throw "Failed to acquire Dell OAuth token: $($_.Exception.Message)"
}

if (-not $tokenResponse -or -not $tokenResponse.access_token) {
    throw "Dell OAuth token response did not contain access_token."
}
$accessToken = $tokenResponse.access_token
Write-Verbose "Dell OAuth Token acquired successfully (Expires in $($tokenResponse.expires_in)s)."

# 6. Query Asset Entitlements Endpoint
$queryUri = "$warrantyUrl`?servicetags=$targetTag"
Write-Verbose "Querying Dell Warranty API: $queryUri"
$apiHeaders = @{
    'Authorization' = "Bearer $accessToken"
    'Accept'        = 'application/json'
}

$apiData = $null
try {
    $apiData = Invoke-RestMethod -Uri $queryUri -Method Get -Headers $apiHeaders -ErrorAction Stop
} catch {
    throw "Dell Warranty API request failed for '$targetTag': $($_.Exception.Message)"
}

if (-not $apiData) {
    throw "Dell Warranty API returned empty response for '$targetTag'."
}

# 7. Helper: ConvertTo-UtcDateTime (handles DateTime, DateTimeOffset, and culture-invariant strings)
function ConvertTo-UtcDateTime {
    param($InputDate)
    if (-not $InputDate) { return $null }
    if ($InputDate -is [System.DateTimeOffset]) { return $InputDate.UtcDateTime }
    if ($InputDate -is [System.DateTime]) { return $InputDate.ToUniversalTime() }
    $parsedOffset = [System.DateTimeOffset]::MinValue
    if ([System.DateTimeOffset]::TryParse([string]$InputDate, [ref]$parsedOffset)) { return $parsedOffset.UtcDateTime }
    $parsedDt = [System.DateTime]::MinValue
    if ([System.DateTime]::TryParse([string]$InputDate, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal, [ref]$parsedDt)) { return $parsedDt }
    return $null
}

$nowUtc = [datetime]::UtcNow
$shipDateUtc = ConvertTo-UtcDateTime $apiData.shipDate
$shipDateStr = if ($shipDateUtc) { $shipDateUtc.ToString('yyyy-MM-dd') } else { [string]$apiData.shipDate }

# Calculate Device Age
$deviceAgeDays = if ($shipDateUtc) { [math]::Round(($nowUtc - $shipDateUtc).TotalDays, 0) } else { $null }
$deviceAgeYears = if ($deviceAgeDays) { [math]::Round($deviceAgeDays / 365.25, 1) } else { $null }

# Process Entitlements Array
$entitlementList = [System.Collections.Generic.List[PSCustomObject]]::new()
$latestEndDateUtc = [datetime]::MinValue
$primaryServiceLevel = 'None / Expired'

if ($apiData.entitlements) {
    foreach ($ent in $apiData.entitlements) {
        $startUtc = ConvertTo-UtcDateTime $ent.startDate
        $endUtc = ConvertTo-UtcDateTime $ent.endDate

        $isActive = ($endUtc -and $endUtc -gt $nowUtc)

        if ($endUtc -and $endUtc -gt $latestEndDateUtc) {
            $latestEndDateUtc = $endUtc
            $primaryServiceLevel = $ent.serviceLevelDescription
        }

        $entitlementList.Add([PSCustomObject]@{
            ServiceLevelDescription = $ent.serviceLevelDescription
            EntitlementType         = $ent.entitlementType
            StartDate               = if ($startUtc) { $startUtc.ToString('yyyy-MM-dd') } else { [string]$ent.startDate }
            EndDate                 = if ($endUtc) { $endUtc.ToString('yyyy-MM-dd') } else { [string]$ent.endDate }
            ServiceLevelCode        = $ent.serviceLevelCode
            ServiceLevelGroup       = $ent.serviceLevelGroup
            ItemNumber              = $ent.itemNumber
            Status                  = if ($isActive) { 'Active' } else { 'Expired' }
        })
    }
}

# 8. Compute Warranty Posture & Hardware Refresh Determination
$isUnderWarranty = ($latestEndDateUtc -gt $nowUtc)
$daysRemaining = if ($latestEndDateUtc -ne [datetime]::MinValue) { [math]::Round(($latestEndDateUtc - $nowUtc).TotalDays, 0) } else { 0 }
$warrantyEndDateStr = if ($latestEndDateUtc -ne [datetime]::MinValue) { $latestEndDateUtc.ToString('yyyy-MM-dd') } else { 'N/A' }

$warrantyStatus = 'Expired'
$verdictCategory = 'REFRESH RECOMMENDED'
$verdictDetails = ''

if ($isUnderWarranty) {
    if ($daysRemaining -le 90) {
        $warrantyStatus = "Expiring Soon ($daysRemaining days remaining)"
        $verdictCategory = 'REFRESH PLANNING (EXPIRING SOON)'
        $verdictDetails = "Device warranty expires in $daysRemaining days on $warrantyEndDateStr ($primaryServiceLevel). Recommend scheduling hardware replacement cycle within current fiscal quarter."
    } else {
        $warrantyStatus = "Active ($daysRemaining days remaining)"
        $verdictCategory = 'ELIGIBLE FOR DEPLOYMENT (DO NOT REFRESH)'
        $verdictDetails = "Device has $daysRemaining days of active vendor support remaining through $warrantyEndDateStr ($primaryServiceLevel). Hardware is fully covered under SLA and eligible for user redeployment."
    }
} else {
    $expiredDaysAgo = [math]::Abs($daysRemaining)
    $warrantyStatus = "Expired ($expiredDaysAgo days ago)"
    $verdictCategory = 'REFRESH RECOMMENDED (OUT OF WARRANTY)'
    $verdictDetails = "Device warranty expired on $warrantyEndDateStr ($expiredDaysAgo days out of warranty; age: $deviceAgeYears years). Vendor SLA and repair coverage have lapsed. Strong candidate for hardware refresh and decommissioning."
}

# 9. Structured Summary Object
$warrantySummary = [PSCustomObject]@{
    ServiceTag              = $apiData.serviceTag
    SystemModel             = if ($apiData.systemDescription) { $apiData.systemDescription } else { $apiData.productLineDescription }
    ProductFamily           = $apiData.productFamily
    ProductLineDescription  = $apiData.productLineDescription
    ProductLobDescription   = $apiData.productLobDescription
    CountryCode             = $apiData.countryCode
    ShipDate                = $shipDateStr
    DeviceAgeYears          = $deviceAgeYears
    DeviceAgeDays           = $deviceAgeDays
    IsUnderWarranty         = $isUnderWarranty
    WarrantyStatus          = $warrantyStatus
    DaysRemaining           = $daysRemaining
    WarrantyEndDate         = $warrantyEndDateStr
    PrimaryServiceLevel     = $primaryServiceLevel
    RefreshVerdict          = $verdictCategory
    RefreshRecommendation   = $verdictDetails
    Entitlements            = $entitlementList
    QueriedAt               = $nowUtc.ToString('yyyy-MM-dd HH:mm:ss UTC')
}

# 10. CSV Export if requested
if ($ExportCsv) {
    $csvTarget = $ExportCsv
    $csvDir = Split-Path -Path $csvTarget -Parent
    if ($csvDir -and -not (Test-Path $csvDir)) {
        New-Item -Path $csvDir -ItemType Directory -Force | Out-Null
    }
    $flatObj = [PSCustomObject]@{
        ServiceTag            = $warrantySummary.ServiceTag
        SystemModel           = $warrantySummary.SystemModel
        ShipDate              = $warrantySummary.ShipDate
        DeviceAgeYears        = $warrantySummary.DeviceAgeYears
        IsUnderWarranty       = $warrantySummary.IsUnderWarranty
        WarrantyStatus        = $warrantySummary.WarrantyStatus
        DaysRemaining         = $warrantySummary.DaysRemaining
        WarrantyEndDate       = $warrantySummary.WarrantyEndDate
        PrimaryServiceLevel   = $warrantySummary.PrimaryServiceLevel
        RefreshVerdict        = $warrantySummary.RefreshVerdict
        RefreshRecommendation = $warrantySummary.RefreshRecommendation
        QueriedAt             = $warrantySummary.QueriedAt
    }
    $flatObj | Export-Csv -Path $csvTarget -NoTypeInformation -Force
    Write-Host " [Dell Warranty] Audit exported to: '$csvTarget'" -ForegroundColor Green
}

# 11. Format Output
switch ($OutputFormat) {
    'Json' {
        return ($warrantySummary | ConvertTo-Json -Depth 5)
    }
    'Markdown' {
        $md = @"
# 🏷️ Dell Asset Warranty & Hardware Refresh Assessment: $($warrantySummary.ServiceTag)

> **Model:** $($warrantySummary.SystemModel)  
> **Service Tag:** ``$($warrantySummary.ServiceTag)``  
> **Lifecycle Verdict:** **$($warrantySummary.RefreshVerdict)**  
> **Warranty Status:** $($warrantySummary.WarrantyStatus)  
> **Ship Date:** $($warrantySummary.ShipDate) ($($warrantySummary.DeviceAgeYears) Years Old)  
> **Coverage End Date:** $($warrantySummary.WarrantyEndDate)  

---

## ⚡ Hardware Refresh Determination
$($warrantySummary.RefreshRecommendation)

---

## 📋 Entitlements & Service Contracts Breakdown
| Service Level Description | Type | Start Date | End Date | Status |
| :--- | :--- | :--- | :--- | :--- |
$($warrantySummary.Entitlements | ForEach-Object { "| $($_.ServiceLevelDescription) | $($_.EntitlementType) | $($_.StartDate) | $($_.EndDate) | $($_.Status) |" } | Out-String).TrimEnd()

---
*Generated autonomously via Dell Technologies Enterprise Warranty API (v5)*
"@
        return $md
    }
    'Table' {
        Write-Host "`n==========================================================================" -ForegroundColor Cyan
        Write-Host " 🏷️ DELL ASSET WARRANTY & REFRESH ASSESSMENT: $($warrantySummary.ServiceTag)" -ForegroundColor Cyan
        Write-Host "==========================================================================" -ForegroundColor Cyan
        Write-Host " Machine Model         : " -NoNewline; Write-Host $warrantySummary.SystemModel -ForegroundColor White
        Write-Host " Product Line          : " -NoNewline; Write-Host $warrantySummary.ProductLineDescription -ForegroundColor White
        Write-Host " Factory Ship Date     : " -NoNewline; Write-Host "$($warrantySummary.ShipDate) ($($warrantySummary.DeviceAgeYears) years old)" -ForegroundColor White
        Write-Host " Country / Region      : " -NoNewline; Write-Host $warrantySummary.CountryCode -ForegroundColor White
        Write-Host " Active Contract       : " -NoNewline; Write-Host $warrantySummary.PrimaryServiceLevel -ForegroundColor White
        Write-Host " Coverage End Date     : " -NoNewline; Write-Host $warrantySummary.WarrantyEndDate -ForegroundColor White
        Write-Host " Warranty Posture      : " -NoNewline
        if ($warrantySummary.IsUnderWarranty) {
            Write-Host $warrantySummary.WarrantyStatus -ForegroundColor Green
        } else {
            Write-Host $warrantySummary.WarrantyStatus -ForegroundColor Red
        }
        Write-Host "--------------------------------------------------------------------------" -ForegroundColor DarkGray
        Write-Host " 🎯 REFRESH VERDICT    : " -NoNewline
        if ($warrantySummary.IsUnderWarranty) {
            Write-Host $warrantySummary.RefreshVerdict -ForegroundColor Green
        } else {
            Write-Host $warrantySummary.RefreshVerdict -ForegroundColor Red
        }
        Write-Host " ℹ️ Details            : $($warrantySummary.RefreshRecommendation)" -ForegroundColor Gray
        Write-Host "--------------------------------------------------------------------------" -ForegroundColor DarkGray
        Write-Host " 📋 Contract Entitlements ($($warrantySummary.Entitlements.Count) Total):" -ForegroundColor Cyan
        $warrantySummary.Entitlements | Format-Table ServiceLevelDescription, EntitlementType, StartDate, EndDate, Status -AutoSize
        return $warrantySummary
    }
    Default {
        return $warrantySummary
    }
}
