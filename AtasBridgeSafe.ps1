param([switch]$Install, [switch]$IncludeHighVolume, [switch]$CatchUp)

# Read-only CashReaper -> Shtab bridge. Run once with -Install under the same
# Windows account that runs ATAS. No trading API credentials are used.
$ErrorActionPreference = 'Stop'
$baseUrl = 'https://petrryzhkov-1786016000.orkestor.ru'
$taskId = '41'
$taskName = 'CashReaper ATAS Data Bridge'
$dataDir = Join-Path $env:APPDATA 'CashReaper'
$secretPath = Join-Path $dataDir 'AtasBridge.secret'
$statePath = Join-Path $dataDir 'AtasBridge.state.json'
$logPath = Join-Path $dataDir 'AtasBridge.log'
$maxChunkBytes = 256 * 1024
$maxUploadsPerRun = if ($IncludeHighVolume) { 4 } else { 2 }
$maxBytesPerDay = 8 * 1024 * 1024
$maxBacklogBytes = 16 * 1024 * 1024
$tailBytesOnBacklog = 2 * 1024 * 1024

New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

function Write-BridgeLog([string]$message) {
    Add-Content -LiteralPath $logPath -Value ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message) -Encoding UTF8
}

if ($Install) {
    if (-not (Test-Path -LiteralPath $secretPath)) {
        Write-Host 'Введите пароль вашего Штаба. Он останется на этом ПК и будет зашифрован для вашей учётной записи Windows.'
        $securePassword = Read-Host 'Пароль Штаба' -AsSecureString
        if ($securePassword.Length -eq 0) { throw 'Пустой пароль не допускается.' }
        $securePassword | ConvertFrom-SecureString | Set-Content -LiteralPath $secretPath -Encoding ASCII
    }

    $scriptPath = $MyInvocation.MyCommand.Path
    if (-not $scriptPath) { throw 'Запустите AtasBridge.ps1 из файла, а не вставкой команд.' }
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $scriptPath
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
        -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)
    $account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 4) -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host 'Мост установлен. Пока вы вошли в Windows, новые данные будут отправляться каждые 5 минут.'
    Write-Host ('Журнал: {0}' -f $logPath)
    exit 0
}

if (-not (Test-Path -LiteralPath $secretPath)) {
    throw 'Пароль не настроен. Запустите AtasBridge.ps1 -Install.'
}

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$securePassword = (Get-Content -LiteralPath $secretPath -Raw).Trim() | ConvertTo-SecureString
$credential = [System.Management.Automation.PSCredential]::new('dashboard', $securePassword)
$password = $credential.GetNetworkCredential().Password

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $true
$handler.AllowAutoRedirect = $true
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(90)

try {
    $loginFields = [System.Collections.Generic.Dictionary[string,string]]::new()
    $loginFields.Add('password', $password)
    $login = [System.Net.Http.FormUrlEncodedContent]::new($loginFields)
    $loginResponse = $client.PostAsync($baseUrl + '/login', $login).GetAwaiter().GetResult()
    $loginResponse.EnsureSuccessStatusCode() | Out-Null
    $check = $client.GetAsync($baseUrl + '/api/data').GetAwaiter().GetResult()
    if (-not $check.IsSuccessStatusCode) {
        throw ('Штаб не принял пароль: HTTP {0}' -f [int]$check.StatusCode)
    }

    $state = if (Test-Path -LiteralPath $statePath) {
        Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{ files = @() }
    }
    if ($null -eq $state.files) { $state.files = @() }
    if ($null -eq $state.PSObject.Properties['day']) {
        $state | Add-Member -NotePropertyName day -NotePropertyValue ''
    }
    if ($null -eq $state.PSObject.Properties['uploadedTodayBytes']) {
        $state | Add-Member -NotePropertyName uploadedTodayBytes -NotePropertyValue 0L
    }
    $today = (Get-Date).ToString('yyyy-MM-dd')
    if ($state.day -ne $today) {
        $state.day = $today
        $state.uploadedTodayBytes = 0L
    }
    $uploadsThisRun = 0

    # Summary and closed bars first. Tick/depth and footprint streams are opt-in.
    $files = Get-ChildItem -LiteralPath $dataDir -Filter 'CashReaper*-v1.csv' -File |
        Where-Object { $IncludeHighVolume -or $_.Name -match '^CashReaper(Stats|Bars)-' } |
        Sort-Object @{ Expression = { if ($_.Name -match '^CashReaperStats-') { 0 } else { 1 } } }, Name

    foreach ($file in $files) {
        if ($uploadsThisRun -ge $maxUploadsPerRun -or
            [long]$state.uploadedTodayBytes -ge $maxBytesPerDay) { break }
        $entry = @($state.files | Where-Object { $_.path -eq $file.FullName }) | Select-Object -First 1
        if ($null -eq $entry) {
            $entry = [pscustomobject]@{ path = $file.FullName; offset = 0L }
            $state.files = @($state.files) + @($entry)
        }

        $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($stream.Length -lt [long]$entry.offset) { $entry.offset = 0L }
            if (-not $CatchUp -and $stream.Length - [long]$entry.offset -gt $maxBacklogBytes) {
                $oldOffset = [long]$entry.offset
                $stream.Position = [Math]::Max(0L, $stream.Length - $tailBytesOnBacklog)
                if ($stream.Position -gt 0) {
                    while ($stream.Position -lt $stream.Length -and $stream.ReadByte() -ne 10) { }
                }
                $entry.offset = $stream.Position
                Write-BridgeLog ('Большой архив {0}: пропущены байты {1}-{2}; данные остались локально. Для отправки архива вручную используйте -CatchUp.' -f $file.Name, $oldOffset, $entry.offset)
            }
            $stream.Position = 0
            $headerBytes = New-Object byte[] 8192
            $headerCount = $stream.Read($headerBytes, 0, $headerBytes.Length)
            $headerEnd = [Array]::IndexOf($headerBytes, [byte]10, 0, $headerCount)
            if ($headerEnd -lt 0) { continue }
            $headerLength = $headerEnd + 1

            while ($stream.Length -gt [long]$entry.offset -and
                   $uploadsThisRun -lt $maxUploadsPerRun -and
                   [long]$state.uploadedTodayBytes -lt $maxBytesPerDay) {
                $offset = [long]$entry.offset
                $stream.Position = $offset
                $budget = [Math]::Min($maxChunkBytes, $maxBytesPerDay - [long]$state.uploadedTodayBytes)
                $buffer = New-Object byte[] ([int]$budget)
                $count = $stream.Read($buffer, 0, $buffer.Length)
                if ($count -eq 0) { break }

                $lastNewline = [Array]::LastIndexOf($buffer, [byte]10, $count - 1, $count)
                if ($lastNewline -lt 0) { break }
                $chunkLength = $lastNewline + 1
                if ($offset -eq 0 -and $chunkLength -le $headerLength) { break }

                $prefix = if ($offset -eq 0) { 0 } else { $headerLength }
                $payload = New-Object byte[] ($prefix + $chunkLength)
                if ($prefix -gt 0) { [Array]::Copy($headerBytes, 0, $payload, 0, $prefix) }
                [Array]::Copy($buffer, 0, $payload, $prefix, $chunkLength)

                $endOffset = $offset + $chunkLength
                $uploadName = '{0}-{1}-{2}.csv' -f $file.BaseName, $offset, $endOffset
                $form = [System.Net.Http.MultipartFormDataContent]::new()
                $form.Add([System.Net.Http.StringContent]::new('task'), 'entity_type')
                $form.Add([System.Net.Http.StringContent]::new($taskId), 'entity_id')
                $form.Add([System.Net.Http.StringContent]::new($uploadName), 'title')
                $form.Add([System.Net.Http.StringContent]::new('Automatic ATAS bridge upload'), 'description')
                $content = [System.Net.Http.ByteArrayContent]::new($payload)
                $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/csv')
                $form.Add($content, 'file', $uploadName)

                try {
                    $response = $client.PostAsync($baseUrl + '/upload', $form).GetAwaiter().GetResult()
                    $response.EnsureSuccessStatusCode() | Out-Null
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    if ($body -notmatch '/view/artifact/[0-9]+') {
                        throw 'Сервер не подтвердил сохранение файла.'
                    }
                } finally {
                    $form.Dispose()
                }

                $entry.offset = $endOffset
                $state.uploadedTodayBytes = [long]$state.uploadedTodayBytes + $payload.Length
                $uploadsThisRun++
                $nextStatePath = $statePath + '.next'
                $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $nextStatePath -Encoding UTF8
                Move-Item -LiteralPath $nextStatePath -Destination $statePath -Force
                Write-BridgeLog ('Отправлено {0}: байты {1}-{2}' -f $file.Name, $offset, $endOffset)
                # Give the next file a turn; otherwise a busy stats stream starves bars.
                break
            }
        } finally {
            $stream.Dispose()
        }
    }

    # Persist skipped-backlog offsets even if no complete new CSV line was uploaded.
    $nextStatePath = $statePath + '.next'
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $nextStatePath -Encoding UTF8
    Move-Item -LiteralPath $nextStatePath -Destination $statePath -Force
} catch {
    Write-BridgeLog ('ОШИБКА: ' + $_.Exception.Message)
    throw
} finally {
    $client.Dispose()
    $handler.Dispose()
}
