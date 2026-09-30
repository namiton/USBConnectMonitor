# Copyright (c) 2026 @Namiton
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
  USB デバイスが認識したりしなかったりする原因を診断する。

.DESCRIPTION
  Windows イベントログ（Kernel-PnP）から USB デバイスの「突然の取り外し」「列挙失敗」を集計し、
  スリープ/起動などの電源遷移に伴う正常な切断を除外したうえで、デバイスごとに原因の仮説を出す。
  -Watch を付けると接続/切断をリアルタイムに監視する（ケーブルを揺らす・ポートを替える等の切り分け用）。
  管理者権限は不要。何も変更しない（読み取り専用）。

.EXAMPLE
  .\UsbDiag.ps1               # 過去14日を診断
  .\UsbDiag.ps1 -Days 3       # 過去3日を診断
  .\UsbDiag.ps1 -Watch        # リアルタイム監視（Ctrl+C で終了）
#>
[CmdletBinding()]
param(
    [int]$Days = 14,
    [switch]$Watch,
    [int]$Top = 5,
    [string]$ReportDir
)

$ErrorActionPreference = 'Stop'
# PowerShell 5.1 では param の既定値内で $PSScriptRoot が空になるため本体で決める
if (-not $ReportDir) { $ReportDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'reports' }

# ---------------------------------------------------------------- 出力ヘルパー
# デバイス名は USB 機器自身が申告する文字列。ESC などの制御文字でターミナル表示を
# 書き換えられないよう、画面・ファイルに出す前に制御文字を取り除く
function Protect-Text([string]$Text) {
    if ($null -eq $Text) { return $null }
    ($Text -replace '[\p{Cc}]', '').Trim()
}
# CSV を Excel で開いたとき数式として実行されないよう、= + - @ で始まるセルを文字列扱いにする
function Protect-CsvCell([string]$Text) {
    $t = Protect-Text $Text
    if ($t -match '^[=+\-@]') { "'" + $t } else { $t }
}

$script:ReportLines = New-Object System.Collections.Generic.List[string]
function Out-Line {
    param([string]$Text = '', [string]$Color = 'Gray')
    $Text = ($Text -replace '[\p{Cc}]', '')
    Write-Host $Text -ForegroundColor $Color
    $script:ReportLines.Add($Text)
}
function Out-Section([string]$Title) {
    Out-Line ''
    Out-Line ('=' * 70) 'DarkCyan'
    Out-Line " $Title" 'Cyan'
    Out-Line ('=' * 70) 'DarkCyan'
}

# ---------------------------------------------------------------- デバイス情報
$script:NameCache = @{}
function Get-DevProp([string]$InstanceId, [string]$Key) {
    try { (Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $Key -ErrorAction Stop).Data } catch { $null }
}
function Get-DevInfo([string]$InstanceId) {
    if ($script:NameCache.ContainsKey($InstanceId)) { return $script:NameCache[$InstanceId] }
    $dev = Get-PnpDevice -InstanceId $InstanceId -ErrorAction SilentlyContinue
    $busDesc = Get-DevProp $InstanceId 'DEVPKEY_Device_BusReportedDeviceDesc'
    $parent = Get-DevProp $InstanceId 'DEVPKEY_Device_Parent'
    $info = [pscustomobject]@{
        InstanceId = $InstanceId
        Name       = Protect-Text $(if ($busDesc) { $busDesc } elseif ($dev) { $dev.FriendlyName } else { '(不明なデバイス)' })
        Class      = if ($dev) { $dev.Class } else { '' }
        Present    = [bool]($dev -and $dev.Present)
        Status     = if ($dev) { $dev.Status } else { '' }
        Location   = Get-DevProp $InstanceId 'DEVPKEY_Device_LocationInfo'
        Parent     = $parent
        ParentName = if ($parent) { Protect-Text (Get-PnpDevice -InstanceId $parent -ErrorAction SilentlyContinue).FriendlyName } else { $null }
        Controller = $null
    }
    # 親をたどって USB ホストコントローラーを特定
    $p = $parent
    for ($i = 0; $p -and $i -lt 8; $i++) {
        if ($p -notlike 'USB\*') {
            $info.Controller = Protect-Text (Get-PnpDevice -InstanceId $p -ErrorAction SilentlyContinue).FriendlyName
            break
        }
        $p = Get-DevProp $p 'DEVPKEY_Device_Parent'
    }
    $script:NameCache[$InstanceId] = $info
    $info
}

# VID_0000 は列挙（認識処理）に失敗したデバイスに Windows が付ける仮 ID
function Test-EnumFailure([string]$InstanceId) { $InstanceId -like 'USB\VID_0000&PID_*' }

function Get-EventData($Event) {
    $d = @{}
    foreach ($n in ([xml]$Event.ToXml()).Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    $d
}

# ---------------------------------------------------------------- 監視モード
function Start-Watch {
    $logDir = $ReportDir
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
    $logPath = Join-Path $logDir ('watch_{0}.log' -f (Get-Date -Format 'yyyyMMdd_HHmmss'))

    function Get-UsbSnapshot {
        $map = @{}
        Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPDeviceID LIKE 'USB\\%'" |
            Where-Object { $_.PNPDeviceID -notmatch '&MI_' -and $_.PNPDeviceID -notlike 'USB\ROOT_HUB*' } |
            ForEach-Object { $map[$_.PNPDeviceID] = $_ }
        $map
    }
    function Write-WatchLine([string]$Text, [string]$Color, [datetime]$At = (Get-Date)) {
        $line = '{0}  {1}' -f $At.ToString('HH:mm:ss.fff'), (Protect-Text $Text)
        Write-Host $line -ForegroundColor $Color
        Add-Content -Path $logPath -Value $line -Encoding UTF8
    }

    Write-Host 'USB 接続/切断をリアルタイム監視します（Ctrl+C で終了）' -ForegroundColor Cyan
    Write-Host "ログ: $logPath" -ForegroundColor DarkGray
    Write-Host '切り分けのコツ: ケーブルやコネクタを軽く揺らす / 別ポートに挿し替える / 他の機器を抜く などを試し、切断が出るか見る' -ForegroundColor DarkGray
    Write-Host ''

    $prev = Get-UsbSnapshot
    foreach ($k in $prev.Keys) {
        $d = $prev[$k]
        Write-WatchLine ('[接続中] {0}  ({1})' -f $d.Name, $k) 'DarkGray'
    }
    $lastOff = @{}
    $counts = @{}
    # 1 秒未満の瞬断は一覧のポーリングでは取りこぼすため、切断はイベントログ(1010/1011)から拾う
    $seenRecords = New-Object System.Collections.Generic.HashSet[long]
    $evSince = Get-Date
    while ($true) {
        Start-Sleep -Milliseconds 500
        try {
            $newEv = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Kernel-PnP/Device Management'; Id = 1010, 1011; StartTime = $evSince.AddSeconds(-2) } -ErrorAction Stop |
                Sort-Object RecordId)
        } catch { $newEv = @() }
        foreach ($e in $newEv) {
            if (-not $seenRecords.Add($e.RecordId)) { continue }
            $inst = (Get-EventData $e).DeviceInstanceId
            if ($inst -notlike 'USB\*' -or $inst -like 'USB\ROOT_HUB*') { continue }
            $lastOff[$inst] = $e.TimeCreated
            $counts[$inst] = 1 + [int]$counts[$inst]
            $why = if ($e.Id -eq 1011) { 'デバイスが障害を報告' } else { 'バス上から消えた' }
            Write-WatchLine ('[切断] {0}  ({1})  {2} / 累計{3}回' -f (Get-DevInfo $inst).Name, $inst, $why, $counts[$inst]) 'Red' $e.TimeCreated
        }
        if ($newEv.Count) { $evSince = ($newEv | Select-Object -Last 1).TimeCreated }
        $cur = Get-UsbSnapshot
        foreach ($k in $cur.Keys) {
            if (-not $prev.ContainsKey($k)) {
                $note = ''
                if ($lastOff.ContainsKey($k)) { $note = '  / 切断から約 {0:N0} 秒で復帰' -f ((Get-Date) - $lastOff[$k]).TotalSeconds }
                $color = 'Green'
                if (Test-EnumFailure $k) { $color = 'Yellow'; $note += '  ※認識失敗（ケーブル・給電・接点を疑う）' }
                Write-WatchLine ('[接続] {0}  ({1}){2}' -f $cur[$k].Name, $k, $note) $color
            }
            elseif ([int]$cur[$k].ConfigManagerErrorCode -notin 0, 45 -and [int]$prev[$k].ConfigManagerErrorCode -eq 0) {
                # 45 は「未接続（切断中）」の状態で、切断はイベントログ側で表示済み
                Write-WatchLine ('[異常] {0}  エラーコード {1}' -f $cur[$k].Name, [int]$cur[$k].ConfigManagerErrorCode) 'Yellow'
            }
        }
        $prev = $cur
    }
}

if ($Watch) { Start-Watch; return }

# ---------------------------------------------------------------- イベント収集
$since = (Get-Date).AddDays(-$Days)
Write-Host "イベントログを読み込み中（過去 $Days 日）..." -ForegroundColor DarkGray

function Read-Events([hashtable]$Filter) {
    try { @(Get-WinEvent -FilterHashtable $Filter -ErrorAction Stop) } catch { @() }
}

# 1010: バス上に存在しないと報告され突然取り外された / 1011: 障害報告により突然取り外された
$removals = Read-Events @{ LogName = 'Microsoft-Windows-Kernel-PnP/Device Management'; Id = 1010, 1011; StartTime = $since } |
    ForEach-Object {
        $d = Get-EventData $_
        [pscustomobject]@{ Time = $_.TimeCreated; Kind = $_.Id; InstanceId = $d.DeviceInstanceId }
    } | Where-Object { $_.InstanceId -like 'USB\*' -and $_.InstanceId -notlike 'USB\ROOT_HUB*' }

# 411: 開始時の問題
$startProblems = Read-Events @{ LogName = 'Microsoft-Windows-Kernel-PnP/Configuration'; Id = 411; StartTime = $since } |
    ForEach-Object {
        $d = Get-EventData $_
        [pscustomobject]@{ Time = $_.TimeCreated; InstanceId = $d.DeviceInstanceId; Problem = $d.Problem; Status = $d.Status }
    } | Where-Object { $_.InstanceId -like 'USB*' -or $_.InstanceId -like 'HID*' }

# 電源遷移（スリープ・復帰・起動・シャットダウン・モダンスタンバイ）
$powerEvents = @()
$powerEvents += Read-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 42, 107, 109, 41, 506, 507; StartTime = $since }
$powerEvents += Read-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-General'; Id = 12, 13; StartTime = $since }
$powerEvents += Read-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Power-Troubleshooter'; Id = 1; StartTime = $since }
$powerTimes = @($powerEvents | ForEach-Object { $_.TimeCreated } | Sort-Object)

$PowerWindowSec = 90
function Test-NearPower([datetime]$t) {
    foreach ($p in $powerTimes) {
        if ([Math]::Abs(($t - $p).TotalSeconds) -le $PowerWindowSec) { return $true }
    }
    $false
}

foreach ($r in $removals) {
    $r | Add-Member -NotePropertyName NearPower -NotePropertyValue (Test-NearPower $r.Time)
}
$abnormal = @($removals | Where-Object { -not $_.NearPower } | Sort-Object Time)

# 同時切断（±3秒以内に別デバイスも切れた）を判定
$SimulSec = 3
$MinCountForJudge = 3
for ($i = 0; $i -lt $abnormal.Count; $i++) {
    $others = New-Object System.Collections.Generic.HashSet[string]
    for ($j = $i - 1; $j -ge 0 -and ($abnormal[$i].Time - $abnormal[$j].Time).TotalSeconds -le $SimulSec; $j--) {
        if ($abnormal[$j].InstanceId -ne $abnormal[$i].InstanceId) { [void]$others.Add($abnormal[$j].InstanceId) }
    }
    for ($j = $i + 1; $j -lt $abnormal.Count -and ($abnormal[$j].Time - $abnormal[$i].Time).TotalSeconds -le $SimulSec; $j++) {
        if ($abnormal[$j].InstanceId -ne $abnormal[$i].InstanceId) { [void]$others.Add($abnormal[$j].InstanceId) }
    }
    $abnormal[$i] | Add-Member -NotePropertyName CoRemoved -NotePropertyValue @($others)
}

# ---------------------------------------------------------------- 設定の収集
$selectiveSuspend = $null
try {
    $q = powercfg /query SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 2>$null
    $ac = ($q | Select-String -Pattern 'AC.*0x([0-9a-fA-F]+)' | Select-Object -First 1)
    if ($ac) { $selectiveSuspend = [Convert]::ToInt32($ac.Matches[0].Groups[1].Value, 16) -eq 1 }
} catch { }

$powerMgmt = @{}
try {
    Get-CimInstance -Namespace root/wmi -ClassName MSPower_DeviceEnable -ErrorAction Stop | ForEach-Object {
        $powerMgmt[($_.InstanceName -replace '_\d+$', '').ToUpperInvariant()] = $_.Enable
    }
} catch { }
function Get-PowerOff([string]$InstanceId) {
    if (-not $InstanceId) { return $null }
    $k = $InstanceId.ToUpperInvariant()
    if ($powerMgmt.ContainsKey($k)) { $powerMgmt[$k] } else { $null }
}

$ProblemText = @{
    1  = 'デバイス構成が正しくない（ドライバー不整合）'
    3  = 'ドライバーが壊れているかメモリ不足'
    10 = 'デバイスを開始できない（ドライバー/ハードの応答不良）'
    12 = 'リソース不足・競合'
    18 = 'ドライバー再インストールが必要'
    19 = 'レジストリ設定の破損'
    21 = 'デバイス削除処理中'
    22 = '無効化されている'
    24 = 'デバイスが存在しない・正しく動作していない'
    28 = 'ドライバーがインストールされていない'
    31 = 'ドライバーを読み込めない'
    39 = 'ドライバーが壊れているか見つからない'
    43 = 'デバイスが障害を報告して停止（ハード/ファーム/給電不足の典型）'
    52 = 'ドライバーの署名を検証できない'
}

# ---------------------------------------------------------------- レポート: 概要
$reportTime = Get-Date
Out-Section "USB 接続トラブル診断レポート  ($($reportTime.ToString('yyyy/MM/dd HH:mm')))"
Out-Line "対象期間        : 過去 $Days 日（$($since.ToString('yyyy/MM/dd HH:mm')) 以降）"
Out-Line "突然の取り外し  : $($removals.Count) 件（うちスリープ/起動などの電源遷移 ±${PowerWindowSec}秒 に伴うもの $(($removals | Where-Object NearPower).Count) 件は正常扱いで除外）"
Out-Line "異常な切断      : $($abnormal.Count) 件" $(if ($abnormal.Count -gt 0) { 'Yellow' } else { 'Green' })
Out-Line "認識失敗(VID_0000): $(($removals | Where-Object { Test-EnumFailure $_.InstanceId }).Count) 件の切断 / 開始時の問題(411) $($startProblems.Count) 件"
$ssText = if ($null -eq $selectiveSuspend) { '取得できず' } elseif ($selectiveSuspend) { '有効' } else { '無効' }
Out-Line "USB セレクティブサスペンド（現在の電源プラン）: $ssText"

# ---------------------------------------------------------------- レポート: 現在異常のデバイス
$problemNow = @(Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPDeviceID LIKE 'USB\\%'" |
    Where-Object { $_.ConfigManagerErrorCode -ne 0 })
Out-Section '現在エラー状態の USB デバイス'
if ($problemNow.Count -eq 0) { Out-Line '  なし' 'Green' }
foreach ($d in $problemNow) {
    $code = [int]$d.ConfigManagerErrorCode
    $txt = if ($ProblemText.ContainsKey($code)) { $ProblemText[$code] } else { '（説明なし）' }
    Out-Line ("  [コード {0}] {1}" -f $code, $d.Name) 'Yellow'
    Out-Line ("            {0}" -f $txt)
    Out-Line ("            {0}" -f $d.PNPDeviceID) 'DarkGray'
}

# ---------------------------------------------------------------- レポート: デバイス別診断
Out-Section "異常な切断が多いデバイス（上位 $Top）"
$groups = @($abnormal | Group-Object InstanceId | Sort-Object Count -Descending | Select-Object -First $Top)
if ($groups.Count -eq 0) { Out-Line '  異常な切断は記録されていません。' 'Green' }

$rank = 0
foreach ($g in $groups) {
    $rank++
    $info = Get-DevInfo $g.Name
    $ev = @($g.Group | Sort-Object Time)
    $isEnumFail = Test-EnumFailure $g.Name

    # 切断間隔（1時間以上空いたものは別セッションとして除外）
    $gaps = @()
    for ($i = 1; $i -lt $ev.Count; $i++) {
        $s = ($ev[$i].Time - $ev[$i - 1].Time).TotalSeconds
        if ($s -le 3600) { $gaps += $s }
    }
    $median = $null
    if ($gaps.Count -gt 0) { $sorted = $gaps | Sort-Object; $median = $sorted[[int][Math]::Floor($sorted.Count / 2)] }

    $simulCount = @($ev | Where-Object { $_.CoRemoved.Count -gt 0 }).Count
    $simulRatio = if ($ev.Count) { $simulCount / $ev.Count } else { 0 }
    $byDay = $ev | Group-Object { $_.Time.ToString('MM/dd') }
    $worstDay = $byDay | Sort-Object Count -Descending | Select-Object -First 1
    $kind1011 = @($ev | Where-Object Kind -eq 1011).Count

    Out-Line ''
    Out-Line ("#{0}  {1}   異常切断 {2} 回" -f $rank, $info.Name, $ev.Count) 'White'
    Out-Line ("    ID          : {0}" -f $g.Name) 'DarkGray'
    $stateText = if ($info.Present) { "接続中 ($($info.Status))" } else { '現在は未接続' }
    Out-Line ("    状態        : {0}" -f $stateText)
    if ($info.Location) { Out-Line ("    接続位置    : {0}  （親: {1}）" -f $info.Location, $info.ParentName) }
    if ($info.Controller) { Out-Line ("    コントローラ: {0}" -f $info.Controller) }
    Out-Line ("    期間        : {0} ～ {1}" -f $ev[0].Time.ToString('MM/dd HH:mm:ss'), $ev[-1].Time.ToString('MM/dd HH:mm:ss'))
    Out-Line ("    日別        : {0}" -f (($byDay | ForEach-Object { '{0}={1}' -f $_.Name, $_.Count }) -join '  '))
    if ($null -ne $median) { Out-Line ("    切断間隔    : 中央値 {0:N0} 秒" -f $median) }
    Out-Line ("    同時切断    : {0} / {1} 回で他の USB デバイスも ±{2}秒以内に切断" -f $simulCount, $ev.Count, $SimulSec)

    # ---- 仮説
    $hyp = New-Object System.Collections.Generic.List[object]
    function Add-Hyp([string]$Level, [string]$Text, [string]$Evidence, [string[]]$Tries) {
        $hyp.Add([pscustomobject]@{ Level = $Level; Text = $Text; Evidence = $Evidence; Tries = $Tries })
    }

    if ($ev.Count -lt $MinCountForJudge -and -not $isEnumFail) {
        # 手で抜いた場合も同じイベントが残るため、少数回では原因を断定しない
        Add-Hyp '参考' "切断回数が少なく（$($ev.Count) 回）判定材料が不足" `
            '手動の抜き差しでも同じイベントが記録される。心当たりがなければ様子を見る' `
            @('症状が出たら -Watch モードで監視して再現状況を記録する')
    }
    elseif ($isEnumFail) {
        Add-Hyp '高' '電気的な接続不良・給電不足でデバイスを認識できていない' `
            "Windows が VID_0000（認識失敗の仮 ID）を割り当てた: $($info.Name)" `
            @('別のケーブルに交換する', 'PC 背面（マザーボード直結）のポートに挿す', 'バスパワーのハブを使っているならセルフパワーのハブにするか直結にする')
    }
    elseif ($simulRatio -ge 0.5) {
        $coNames = @($ev | ForEach-Object { $_.CoRemoved } | Group-Object | Sort-Object Count -Descending | Select-Object -First 3 |
            ForEach-Object { (Get-DevInfo $_.Name).Name }) -join ' / '
        Add-Hyp '高' 'デバイス単体ではなく、ハブ・コントローラ・給電など上流側の問題' `
            ("切断の {0:P0} で他デバイスも同時に切断（一緒に切れる機器: {1}）" -f $simulRatio, $coNames) `
            @('同時に切れる機器が共通のハブ/フロントパネルにつながっていないか確認し、別系統のポートに分ける', 'USB ハブを外して PC に直結する', '消費電力の大きい機器（HDD・Web カメラ等）を外して再現するか見る', 'マザーボードのチップセット/USB ドライバーを更新する', '（上記で改善しない場合）BIOS/UEFI の更新も検討する。更新中の電源断や中断は PC が起動しなくなる原因になるため、必ずメーカーの手順に従い、ノート PC は AC 電源につないで行う')
    }
    else {
        Add-Hyp '高' 'このデバイス（またはそのケーブル・挿しているポート）単体の問題' `
            ("他デバイスはほぼ同時に切れていない（同時切断 {0:P0}）→ 上流のハブ/コントローラ全体の問題ではない" -f $simulRatio) `
            @('別のポートに挿し替えて改善するか確認（改善すればポート側、しなければデバイス側）', 'ケーブル付きなら別ケーブルで試す', '別の PC に挿して同じ症状が出るか確認（出ればデバイス本体の故障）')
    }

    $nameForMatch = "$($info.Name) $($info.ParentName)"
    if ($nameForMatch -match 'Dongle|Receiver|Wireless|2\.4G|Unifying|Lightspeed|レシーバー|ドングル') {
        Add-Hyp '中' '無線ドングルの電波受信が不安定（干渉・距離）' `
            "デバイス名が無線レシーバー: $($info.Name)" `
            @('付属の延長ケーブル/アダプタでドングルをマウス・キーボードの近く（机の上）に出す', 'USB 3.x ポートや USB3 機器のすぐ隣を避ける（USB3 は 2.4GHz 帯にノイズを出す）', 'Wi-Fi ルーター・他の 2.4GHz 機器から離す', '本体側の電池残量/充電を確認する')
    }
    if ($nameForMatch -match '8K|8000\s*Hz|4K|4000\s*Hz') {
        Add-Hyp '中' '高ポーリングレート（4K/8K Hz）による負荷・相性' `
            "デバイス名に高ポーリング対応の表記: $($info.Name)" `
            @('専用ソフトでポーリングレートを 1000Hz に下げて改善するか確認する', 'ハブ・フロントポートを避け、マザーボード背面のポートに直結する', 'デバイスのファームウェアを更新する')
    }
    if ($median -and $median -le 120 -and $ev.Count -ge 20) {
        Add-Hyp '参考' '短い間隔で繰り返し切断している（常時不安定な状態）' `
            ("中央値 {0:N0} 秒おき。最多日 {1} に {2} 回" -f $median, $worstDay.Name, $worstDay.Count) `
            @('-Watch モードで監視しながら、挿し直し・ポート変更・ケーブル交換の前後で切断頻度を比べる')
    }
    if ($kind1011 -gt 0) {
        Add-Hyp '中' 'デバイス自身が障害を報告している（ファームウェア/ハード不良・給電不足）' `
            "「障害が報告されたため取り外された」(1011) が $kind1011 回" `
            @('デバイスのファームウェア更新', '給電の安定したポート/セルフパワーハブで試す')
    }
    $pmSelf = Get-PowerOff $g.Name
    $pmHub = Get-PowerOff $info.Parent
    if ($ev.Count -ge $MinCountForJudge -and ($selectiveSuspend -or $pmSelf -or $pmHub)) {
        $ev2 = @()
        if ($selectiveSuspend) { $ev2 += 'セレクティブサスペンド有効' }
        if ($pmSelf) { $ev2 += 'このデバイスの「電力の節約のために電源をオフにできる」が有効' }
        if ($pmHub) { $ev2 += '親ハブの「電力の節約のために電源をオフにできる」が有効' }
        Add-Hyp '低' '省電力機能による切断' `
            ($ev2 -join '、') `
            @('電源オプション > USB 設定 > セレクティブサスペンドを「無効」にして改善するか確認', "デバイスマネージャーで親ハブ（$($info.ParentName)）のプロパティ > 電源の管理 のチェックを外す")
    }

    Out-Line '    考えられる原因:' 'Cyan'
    foreach ($h in $hyp) {
        $color = switch ($h.Level) { '高' { 'Red' } '中' { 'Yellow' } default { 'Gray' } }
        Out-Line ("      [{0}] {1}" -f $h.Level, $h.Text) $color
        Out-Line ("           根拠: {0}" -f $h.Evidence) 'DarkGray'
        foreach ($t in $h.Tries) { Out-Line ("           → {0}" -f $t) }
    }
}

# ---------------------------------------------------------------- レポート: 開始時の問題
if ($startProblems.Count -gt 0) {
    Out-Section '開始に失敗した USB/HID デバイス（イベント 411）'
    $startProblems | Group-Object InstanceId | Sort-Object Count -Descending | Select-Object -First $Top | ForEach-Object {
        $last = $_.Group | Sort-Object Time | Select-Object -Last 1
        $code = [Convert]::ToInt32(($last.Problem -replace '^0x', ''), 16)
        $txt = if ($ProblemText.ContainsKey($code)) { $ProblemText[$code] } else { '' }
        Out-Line ("  {0} 回  {1}  問題コード {2} {3}" -f $_.Count, (Get-DevInfo $_.Name).Name, $code, $txt) 'Yellow'
        Out-Line ("         {0}  最終 {1}" -f $_.Name, $last.Time.ToString('MM/dd HH:mm')) 'DarkGray'
    }
}

# ---------------------------------------------------------------- 次の一手
Out-Section '次の切り分け手順'
Out-Line '  1. 上の [高] の原因から順に「→」の対策を 1 つずつ試す（同時に複数変えると原因が分からなくなる）'
Out-Line '  2. 対策後、  UsbDiag.bat -Watch  で監視しながら普段どおり使い、切断が出なくなったか確認する'
Out-Line '  3. 数日後に再度このレポートを出し、日別の切断回数が減ったかで効果を判断する'

# ---------------------------------------------------------------- 保存
try {
    if (-not (Test-Path $ReportDir)) { New-Item -ItemType Directory -Path $ReportDir | Out-Null }
    $reportPath = Join-Path $ReportDir ('report_{0}.txt' -f $reportTime.ToString('yyyyMMdd_HHmmss'))
    $script:ReportLines | Set-Content -Path $reportPath -Encoding UTF8
    $csvPath = [IO.Path]::ChangeExtension($reportPath, '.csv')
    $removals | Sort-Object Time | Select-Object @{n = 'Time'; e = { $_.Time.ToString('yyyy-MM-dd HH:mm:ss') } }, Kind,
        @{n = 'InstanceId'; e = { Protect-CsvCell $_.InstanceId } },
        @{n = 'Name'; e = { Protect-CsvCell (Get-DevInfo $_.InstanceId).Name } }, NearPower |
        Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Write-Host ''
    Write-Host "レポート保存: $reportPath" -ForegroundColor DarkGray
    Write-Host "切断イベント一覧(CSV): $csvPath" -ForegroundColor DarkGray
    Write-Host '※ レポートと CSV には接続機器のシリアル番号が含まれます。人に共有するときは注意してください' -ForegroundColor DarkGray
}
catch {
    Write-Host ''
    Write-Host "レポートを保存できませんでした（$ReportDir）: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host '書き込みできるフォルダに置くか、-ReportDir で保存先を指定してください' -ForegroundColor Yellow
}
