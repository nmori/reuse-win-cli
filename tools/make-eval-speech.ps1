# ストリーミング推論の評価（tests の StreamingEvaluationTest）に使う、きれいな音声を Windows の音声合成で作る。
# 雑音と残響は評価の側で固定の乱数から足すので、ここでは声だけを作る。
#   clean_<Hz>.wav  : 評価する声（日本語と英語、話者を切り替える）
#   babble_<Hz>.wav : 雑踏の雑音に混ぜる、別の話者の声
# Windows PowerShell 5.1（System.Speech が使える方）で実行する:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-eval-speech.ps1 -OutDir <フォルダ>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int[]]$SampleRates = @(48000, 24000, 16000)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null

$clean = @(
    @{ Culture = 'ja-JP'; Text = '本日は晴天なり。マイクのテスト中です。これから、ノイズ除去の遅延と音質を確かめるために、いくつかの文を読み上げます。' },
    @{ Culture = 'en-US'; Text = 'The quick brown fox jumps over the lazy dog. Please call Stella and ask her to bring these things with her from the store.' },
    @{ Culture = 'ja-JP'; Text = '配信中に、エアコンの音やキーボードの打鍵音が入ってしまうことがあります。声だけをはっきりと届けたいときに、この処理が役に立ちます。' },
    @{ Culture = 'en-US'; Text = 'Six spoons of fresh snow peas, five thick slabs of blue cheese, and maybe a snack for her brother Bob.' },
    @{ Culture = 'ja-JP'; Text = 'さて、ここで少し間を置いてから、もう一度話し始めます。短い返事、はい。いいえ。そうですね。ありがとうございました。' }
)
$babble = @(
    @{ Culture = 'en-US'; Text = 'We also need a small plastic snake and a big toy frog for the kids. She can scoop these things into three red bags, and we will go meet her Wednesday at the train station.' },
    @{ Culture = 'ja-JP'; Text = 'あちらの席では、来週の予定について話し合っています。会議室の予約と、資料の準備と、出張の手配を確認しましょう。' }
)

# 一覧には出ても選べない声（別のビット数でしか動かないものなど）があるので、選べるものだけを返す
function Get-Voices([string]$culture) {
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        @($synth.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq $culture } | ForEach-Object { $_.VoiceInfo.Name } | Where-Object {
            try { $synth.SelectVoice($_); $true } catch { $false }
        })
    }
    finally { $synth.Dispose() }
}

function Write-Speech([object[]]$lines, [string]$path, [int]$rate, [int]$voiceOffset) {
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo($rate, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.SetOutputToWaveFile($path, $format)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            # 関数の戻り値は 1 つだけだと配列でなくなるので、@() で配列に戻す
            $voices = @(Get-Voices $lines[$i].Culture)
            if ($voices.Count -eq 0) { throw "$($lines[$i].Culture) の音声合成の声がありません。" }
            $synth.SelectVoice($voices[($i + $voiceOffset) % $voices.Count])
            $synth.Speak($lines[$i].Text)
        }
    }
    finally { $synth.Dispose() }
}

foreach ($rate in $SampleRates) {
    Write-Speech $clean (Join-Path $OutDir "clean_$rate.wav") $rate 0
    Write-Speech $babble (Join-Path $OutDir "babble_$rate.wav") $rate 1
    Write-Host "clean_$rate.wav / babble_$rate.wav"
}
