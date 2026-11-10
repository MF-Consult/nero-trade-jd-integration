<#
Registers the NeroTrade webhook at JD Logistik and stores the returned secret in the Function App,
without ever printing the secret. Run from PowerShell with Azure CLI signed in (az login).

  .\register-jd-webhook.ps1 -ResourceGroup <resource-group-of-nero-trade-data-syncer>

Event types: 0-3 = request-order changes, 30-32 = incoming-shipment changes.
Setting the app setting restarts the Function App (a few seconds, jobs resume on the next tick).
#>
param(
  [Parameter(Mandatory)][string]$ResourceGroup,
  [string]$App = 'nero-trade-data-syncer',
  [string]$BaseUrl = 'https://api.jdlogistik.dk',
  [string]$HookUrl = 'https://nero-trade-data-syncer-d5apejcef0ahbpge.westeurope-01.azurewebsites.net/api/jd/webhook'
)
$ErrorActionPreference = 'Stop'

$secureToken = Read-Host 'JD bearer token' -AsSecureString
$token = [System.Net.NetworkCredential]::new('', $secureToken).Password
$headers = @{ Authorization = "Bearer $token" }

$existing = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/webhooks" -Headers $headers
$already = @($existing) | Where-Object { $_.urlAddress -eq $HookUrl }
if ($already) {
  Write-Host "A webhook for $HookUrl is already registered. Nothing changed." -ForegroundColor Yellow
  $already | Format-List
  return
}

$body = @{ types = @(0,1,2,3,30,31,32); urlAddress = $HookUrl } | ConvertTo-Json
$created = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/webhooks" -Headers $headers -ContentType 'application/json' -Body $body
if (-not $created.secret) { throw 'JD returned no secret. Not changing the Function App.' }

az functionapp config appsettings set -g $ResourceGroup -n $App --settings "JdWebhook__Secret=$($created.secret)" --output none
if ($LASTEXITCODE -ne 0) { throw 'Could not set JdWebhook__Secret. The webhook IS registered at JD; delete it there or set the secret by hand.' }

Write-Host 'Done. Webhook registered and JdWebhook__Secret set (not shown).' -ForegroundColor Green
Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/webhooks" -Headers $headers | Format-List
