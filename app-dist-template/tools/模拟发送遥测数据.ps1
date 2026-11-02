# 模拟 Forza Horizon 6 数据输出发包器
# 用途：在未运行游戏时向 127.0.0.1:23333 以约 60Hz 发送测试遥测数据，
#       用于验证 HUD 各项数值与动画是否正常。
# 用法：powershell -ExecutionPolicy Bypass -File .\模拟发送遥测数据.ps1
#       按 Ctrl+C 停止发送。

$udp = New-Object System.Net.Sockets.UdpClient
$target = [System.Net.IPEndPoint]::new(
    [System.Net.IPAddress]::Parse("127.0.0.1"), 23333)

function New-TelemetryPacket {
    param(
        [float]$rpm,
        [float]$speedMps,
        [int]$gear,
        [int]$steerRaw,
        [byte]$throttle,
        [byte]$brake,
        [float]$slip,
        [float]$suspFL,
        [float]$suspFR,
        [float]$suspRL,
        [float]$suspRR,
        [float]$tempFL,
        [float]$tempFR,
        [float]$tempRL,
        [float]$tempRR,
        [int]$drivetrain
    )

    $p = New-Object byte[] 324

    # IsRaceOn (offset 0, int) = 1
    [BitConverter]::GetBytes([int]1).CopyTo($p, 0)

    # EngineMaxRpm (offset 8) / EngineIdleRpm (offset 12)
    [BitConverter]::GetBytes([float]8000).CopyTo($p, 8)
    [BitConverter]::GetBytes([float]800).CopyTo($p, 12)

    # CurrentEngineRpm (offset 16)
    [BitConverter]::GetBytes($rpm).CopyTo($p, 16)

    # TireCombinedSlip FL/FR/RL/RR (offset 180/184/188/192)
    foreach ($off in 180, 184, 188, 192) {
        [BitConverter]::GetBytes($slip).CopyTo($p, $off)
    }

    # NormalizedSuspensionTravel FL/FR/RL/RR (offset 68/72/76/80)
    [BitConverter]::GetBytes($suspFL).CopyTo($p, 68)
    [BitConverter]::GetBytes($suspFR).CopyTo($p, 72)
    [BitConverter]::GetBytes($suspRL).CopyTo($p, 76)
    [BitConverter]::GetBytes($suspRR).CopyTo($p, 80)

    # 驱动形式 S32 (offset 224)：0=FWD 1=RWD 2=AWD
    [BitConverter]::GetBytes([int]$drivetrain).CopyTo($p, 224)

    # Speed m/s (offset 256)
    [BitConverter]::GetBytes($speedMps).CopyTo($p, 256)

    # Power watts (offset 260) / Torque Nm (offset 264)
    [BitConverter]::GetBytes([float]320000).CopyTo($p, 260)
    [BitConverter]::GetBytes([float]520).CopyTo($p, 264)

    # 四轮胎温 (offset 268/272/276/280)
    [BitConverter]::GetBytes($tempFL).CopyTo($p, 268)
    [BitConverter]::GetBytes($tempFR).CopyTo($p, 272)
    [BitConverter]::GetBytes($tempRL).CopyTo($p, 276)
    [BitConverter]::GetBytes($tempRR).CopyTo($p, 280)

    # 增压 psi (offset 284)，约 1.0 bar
    [BitConverter]::GetBytes([float]14.5).CopyTo($p, 284)

    # 油门/刹车/档位/转向 (offset 315/316/319/320)
    $p[315] = $throttle
    $p[316] = $brake
    $p[319] = [byte]$gear
    $steerByte = $steerRaw
    if ($steerByte -lt 0) { $steerByte += 256 }
    $p[320] = [byte]$steerByte

    return $p
}

Write-Host "开始发送模拟遥测到 127.0.0.1:23333，按 Ctrl+C 停止"
$frame = 0
while ($true) {
    $rpm      = 3000 + 4500 * [math]::Abs([math]::Sin($frame / 120.0))
    $speed    = 40   + 30   * [math]::Abs([math]::Sin($frame / 240.0))
    $steer    = [int][math]::Round(110 * [math]::Sin($frame / 90.0))
    $throttle = [byte](120 + 130 * [math]::Abs([math]::Sin($frame / 150.0)))
    $brake    = [byte][math]::Max(0, [math]::Min(255, 127 + 127 * [math]::Sin($frame / 170.0)))
    $gear     = if (([int][math]::Floor($frame / 300) % 6) -eq 5) { 0 } else { 1 + ([int][math]::Floor($frame / 300) % 5) }
    $slip     = 0.2 + 0.9 * [math]::Abs([math]::Sin($frame / 90.0))
    $suspFL   = 0.5 + 0.25 * [math]::Sin($frame / 45.0)
    $suspFR   = 0.5 + 0.25 * [math]::Sin($frame / 45.0 + 0.7)
    $suspRL   = 0.5 + 0.25 * [math]::Sin($frame / 45.0 + 1.4)
    $suspRR   = 0.5 + 0.25 * [math]::Sin($frame / 45.0 + 2.1)
    # 胎温 0-135℃（冬季起始，华氏 32-275），覆盖 冷/常温/高温/过热 四段
    $tempC    = 67.5 - 67.5 * [math]::Cos($frame / 400.0)
    $tempFL   = 32 + $tempC * 1.8
    $tempFR   = 32 + [math]::Min(135, $tempC + 5) * 1.8
    $tempRL   = 32 + [math]::Max(0, $tempC - 4) * 1.8
    $tempRR   = 32 + [math]::Min(135, $tempC + 8) * 1.8
    $drivetrain = [int][math]::Floor($frame / 300) % 3

    $packet = New-TelemetryPacket -rpm $rpm -speedMps $speed `
        -gear $gear -steerRaw $steer -throttle $throttle -brake $brake -slip $slip `
        -suspFL $suspFL -suspFR $suspFR -suspRL $suspRL -suspRR $suspRR `
        -tempFL $tempFL -tempFR $tempFR -tempRL $tempRL -tempRR $tempRR `
        -drivetrain $drivetrain

    # 扭矩覆盖负值区间（验证松油门时扭矩/马力钳位 0）
    $torque   = 230 + 290 * [math]::Sin($frame / 220.0)
    $power    = [math]::Max(-40000, $torque * 600)
    [BitConverter]::GetBytes([float]$torque).CopyTo($packet, 264)
    [BitConverter]::GetBytes([float]$power).CopyTo($packet, 260)

    # 每 600 帧插入约 0.16 秒 IsRaceOn=0，验证 HUD 重置路径
    if (($frame % 600) -lt 10) {
        [BitConverter]::GetBytes([int]0).CopyTo($packet, 0)
    }

    $clutch    = [byte](127 + 127 * [math]::Sin($frame / 60.0))
    $handbrake = if (($frame % 300) -lt 30) { [byte]255 } else { [byte]0 }
    $packet[317] = $clutch
    $packet[318] = $handbrake

    $udp.Send($packet, $packet.Length, $target) | Out-Null
    $frame++
    Start-Sleep -Milliseconds 16
}
