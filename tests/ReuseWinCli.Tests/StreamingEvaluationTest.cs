using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using MathNet.Numerics.IntegralTransforms;
using NUnit.Framework;
using ReuseWinCli.Audio;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// ストリーミング推論（<see cref="StreamingEnhancer"/>）の評価。音声合成のきれいな声に、固定の乱数で雑音・雑踏・残響を足して補正し、
    /// 今のファイル処理（5 秒のチャンクを半分ずつ重ねる <see cref="ChunkedEnhancer"/>）と比べる。
    /// 声は tools/make-eval-speech.ps1 で作り、そのフォルダを環境変数 REUSE_EVAL_DIR に指定したときだけ走る。
    /// REUSE_EVAL_OUT を指定すると、聞き比べ用の WAV と結果の表（results.tsv）をそこに書く。
    ///
    /// 評価基準（満足ライン）:
    ///   1. 遅延: 実時間の速さで入力したとき、各サンプルが入力されてから補正後の値が出るまでの最大の時間が 1 秒未満。
    ///      そのために、推論 1 回の平均の時間がホップの 8 割未満（処理が入力に追いつき続ける）
    ///   2. 音質: オフラインの処理に比べて、SI-SDR（きれいな声との一致。高いほどよい）の低下が 1dB 以内、
    ///            LSD（8kHz までの対数スペクトルの距離。低いほどよい）の増加が 0.5dB 以内（3 条件の平均）
    /// </summary>
    [Explicit("REUSE_EVAL_DIR の評価用音声と GPU を使い、数分かかる")]
    class StreamingEvaluationTest
    {
        const double ExcerptSeconds = 30;
        const double MaxLatencySeconds = 1.0;
        const double MaxSiSdrLoss = 1.0;
        const double MaxLsdIncrease = 0.5;
        const double ThroughputMargin = 0.8;

        static readonly Lazy<ReUseEngine> SharedEngine = new(() => ReUseEngine.Create(null, false));
        static readonly ConcurrentDictionary<(int, string), float[]> OfflineCache = new();

        sealed record Condition(string Name, float[] Clean, float[] Noisy);

        /// <summary>品質の比較（サンプル数は文脈・ホップ・先読み・クロスフェードの秒数）</summary>
        static IEnumerable<TestCaseData> SweepCases()
        {
            foreach (var context in new[] { 0.5, 1.0, 2.0, 4.0 })
                foreach (var lookahead in new[] { 0.1, 0.2, 0.4 })
                    yield return new TestCaseData(16000, context, 0.2, lookahead).SetName($"Sweep16k_P{context}_B0.2_L{lookahead}");
            // 24kHz・48kHz は、推論 1 回がホップの 8 割に収まる窓の組（RTX 4050 Laptop で 24kHz の 1.5 秒が約 185ms、48kHz の 1.1 秒が約 285ms）
            foreach (var (rate, context, hop, lookahead) in new[] { (24000, 1.0, 0.3, 0.2), (24000, 0.5, 0.3, 0.2), (48000, 0.5, 0.4, 0.2), (48000, 0.3, 0.4, 0.2) })
                yield return new TestCaseData(rate, context, hop, lookahead).SetName($"Sweep{rate / 1000}k_P{context}_B{hop}_L{lookahead}");
        }

        /// <summary>音質と、推論 1 回の時間を測る（実時間の速さには合わせず、できるだけ速く流す）</summary>
        [TestCaseSource(nameof(SweepCases))]
        public void Sweep(int sampleRate, double context, double hop, double lookahead)
        {
            var settings = StreamingSettings.FromSeconds(sampleRate, context, hop, lookahead, Math.Min(hop, lookahead) / 2);
            var timed = new TimedEnhancer(SharedEngine.Value);
            Warmup(sampleRate, settings);

            var rows = new List<double[]>();
            foreach (var condition in LoadConditions(sampleRate))
            {
                var offline = Offline(sampleRate, condition);
                var streamer = new StreamingEnhancer(timed, sampleRate, settings);
                var streamed = streamer.Process(condition.Noisy).Concat(streamer.Flush()).ToArray();
                SaveWav($"{condition.Name}_{sampleRate}_P{context}_B{hop}_L{lookahead}.wav", streamed, sampleRate);
                rows.Add(Report(condition, sampleRate, offline, streamed));
            }

            var steady = timed.Seconds.Skip(1).ToArray();
            var worstStep = steady.Max();
            var estimate = hop + lookahead + worstStep;
            var mean = Average(rows);
            var verdict = Verdict(mean, estimate, steady.Average(), hop);
            var line = string.Join('\t',
                $"{sampleRate}", F(context), F(hop), F(lookahead),
                F(mean[0]), F(mean[1]), F(mean[2]), F(mean[3]), F(mean[4]), F(mean[5]),
                F(steady.Average() * 1000), F(worstStep * 1000), F(estimate), verdict);
            TestContext.Out.WriteLine("rate\tP\tB\tL\tSISDR_noisy\tSISDR_offline\tSISDR_stream\tLSD_offline\tLSD_stream\tSNR_vs_offline\tstep_ms\tstep_max_ms\tlatency_est_s\tverdict");
            TestContext.Out.WriteLine(line);
            AppendResult(line);
        }

        /// <summary>窓の長さごとの推論 1 回の時間（ストリーミングの窓を決める目安。評価用の音声は要らない）</summary>
        [TestCase(16000)]
        [TestCase(24000)]
        [TestCase(48000)]
        public void InferenceTime(int sampleRate)
        {
            var random = new Random(5);
            foreach (var seconds in new[] { 0.5, 1.0, 1.5, 2.0 })
            {
                var input = Enumerable.Range(0, (int)(sampleRate * seconds)).Select(_ => (float)(random.NextDouble() - 0.5) * 0.1f).ToArray();
                SharedEngine.Value.Enhance(input, sampleRate);
                var times = new List<double>();
                for (var trial = 0; trial < 10; trial++)
                {
                    var watch = Stopwatch.StartNew();
                    SharedEngine.Value.Enhance(input, sampleRate);
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                var line = $"inference\t{sampleRate}\t{F(seconds)}s\tmedian {F(times[times.Count / 2])}ms\tmax {F(times[^1])}ms";
                TestContext.Out.WriteLine(line);
                AppendResult(line);
            }
        }

        static IEnumerable<TestCaseData> RealTimeCases()
        {
            yield return new TestCaseData(16000, 1.0, 0.2, 0.2).SetName("RealTime16k_P1.0_B0.2_L0.2");
            yield return new TestCaseData(24000, 1.0, 0.3, 0.2).SetName("RealTime24k_P1.0_B0.3_L0.2");
            yield return new TestCaseData(48000, 0.5, 0.4, 0.2).SetName("RealTime48k_P0.5_B0.4_L0.2");
        }

        /// <summary>
        /// 実時間の速さ（10ms ごと）で入力を渡し、各サンプルの「入力された時刻」から「補正後の値が出た時刻」までを測る。
        /// 推論の間に届いた入力は、次の呼び出しでまとめて渡す（マイクの入力をためておき、別のスレッドで処理するのと同じ）
        /// </summary>
        [TestCaseSource(nameof(RealTimeCases))]
        public void RealTime(int sampleRate, double context, double hop, double lookahead)
        {
            var settings = StreamingSettings.FromSeconds(sampleRate, context, hop, lookahead, Math.Min(hop, lookahead) / 2);
            Warmup(sampleRate, settings);
            var condition = LoadConditions(sampleRate).First(c => c.Name == "babble");
            var input = condition.Noisy;
            var block = sampleRate / 100;
            var streamer = new StreamingEnhancer(SharedEngine.Value, sampleRate, settings);
            var output = new List<float>();
            var latencies = new List<double>();
            var watch = Stopwatch.StartNew();
            var pushed = 0;
            while (pushed < input.Length)
            {
                // 10ms のブロックは、その最後のサンプルの時刻に届く
                var due = (int)Math.Min(input.Length, (long)(watch.Elapsed.TotalSeconds * sampleRate) / block * block);
                if (due <= pushed)
                {
                    Thread.Sleep(1);
                    continue;
                }
                var before = streamer.Emitted;
                output.AddRange(streamer.Process(input.AsSpan(pushed, due - pushed)));
                pushed = due;
                var now = watch.Elapsed.TotalSeconds;
                for (var i = before; i < streamer.Emitted; i += block)
                    latencies.Add(now - (i / block + 1) * (double)block / sampleRate);
            }
            output.AddRange(streamer.Flush());

            latencies.Sort();
            var worst = latencies[^1];
            var p99 = latencies[(int)(latencies.Count * 0.99)];
            var quality = Report(condition, sampleRate, Offline(sampleRate, condition), [.. output]);
            var line = $"realtime\t{sampleRate}\tP={F(context)}\tB={F(hop)}\tL={F(lookahead)}\tmax_latency={F(worst)}s\tp99={F(p99)}s\tmedian={F(latencies[latencies.Count / 2])}s\tSISDR {F(quality[1])}->{F(quality[2])}\tLSD {F(quality[3])}->{F(quality[4])}";
            TestContext.Out.WriteLine(line);
            AppendResult(line);
            Assert.That(worst, Is.LessThan(MaxLatencySeconds), "最大遅延が満足ラインを超えた");
        }

        static string Verdict(double[] mean, double latency, double meanStep, double hop)
        {
            var problems = new List<string>();
            // 推論 1 回がホップより遅いと、処理待ちの入力がたまり続けて遅延が際限なく伸びる。ほかの処理の揺れの分の余裕として 2 割を残す
            if (meanStep >= hop * ThroughputMargin)
                problems.Add("throughput");
            if (latency >= MaxLatencySeconds)
                problems.Add("latency");
            if (mean[1] - mean[2] > MaxSiSdrLoss)
                problems.Add("sisdr");
            if (mean[4] - mean[3] > MaxLsdIncrease)
                problems.Add("lsd");
            return problems.Count == 0 ? "OK" : "NG:" + string.Join(',', problems);
        }

        /// <returns>[SI-SDR 入力, SI-SDR オフライン, SI-SDR ストリーミング, LSD オフライン, LSD ストリーミング, ストリーミングとオフラインの SNR]</returns>
        static double[] Report(Condition condition, int sampleRate, float[] offline, float[] streamed)
        {
            Assert.That(streamed, Has.Length.EqualTo(condition.Noisy.Length));
            double[] row =
            [
                SiSdr(condition.Noisy, condition.Clean), SiSdr(offline, condition.Clean), SiSdr(streamed, condition.Clean),
                Lsd(offline, condition.Clean, sampleRate), Lsd(streamed, condition.Clean, sampleRate),
                Fixture.Snr(streamed, offline),
            ];
            TestContext.Out.WriteLine($"  {condition.Name,-8} SI-SDR noisy {F(row[0])} offline {F(row[1])} stream {F(row[2])} | LSD offline {F(row[3])} stream {F(row[4])} | SNR vs offline {F(row[5])}");
            return row;
        }

        static double[] Average(List<double[]> rows)
            => [.. Enumerable.Range(0, rows[0].Length).Select(i => rows.Average(r => r[i]))];

        /// <summary>シェーダーの作成と作業用バッファの確保を、計測の外で済ませる</summary>
        static void Warmup(int sampleRate, StreamingSettings settings)
            => SharedEngine.Value.Enhance(new float[settings.WindowSamples], sampleRate);

        static float[] Offline(int sampleRate, Condition condition)
            => OfflineCache.GetOrAdd((sampleRate, condition.Name), _ =>
            {
                var output = new MemoryWriter();
                new ChunkedEnhancer(SharedEngine.Value).Process(new MemoryReader(condition.Noisy, sampleRate, 1), output, 1, null, default);
                var result = output.ToArray();
                SaveWav($"{condition.Name}_{sampleRate}_offline.wav", result, sampleRate);
                SaveWav($"{condition.Name}_{sampleRate}_noisy.wav", condition.Noisy, sampleRate);
                return result;
            });

        // ---- 評価用の音声 ----

        static List<Condition> LoadConditions(int sampleRate)
        {
            var directory = Environment.GetEnvironmentVariable("REUSE_EVAL_DIR");
            Assume.That(directory, Is.Not.Null.And.Not.Empty, "REUSE_EVAL_DIR が設定されていません");
            var cleanPath = Path.Combine(directory!, $"clean_{sampleRate}.wav");
            Assume.That(File.Exists(cleanPath), Is.True, $"{cleanPath} がありません");
            var clean = ReadPcm16(cleanPath).Take((int)(ExcerptSeconds * sampleRate)).ToArray();
            var babbleVoice = ReadPcm16(Path.Combine(directory!, $"babble_{sampleRate}.wav"));
            var power = ActivePower(clean);

            var pink = Scale(PinkNoise(clean.Length, new Random(11)), power, 5);
            var babble = Add(
                Scale([.. Enumerable.Range(0, clean.Length).Select(i => babbleVoice[(i + sampleRate * 3) % babbleVoice.Length])], power, 5),
                Scale(PinkNoise(clean.Length, new Random(12)), power, 20));
            var reverberant = Add(Reverb(clean, sampleRate), Add(Scale(Hum(clean.Length, sampleRate), power, 20), Scale(WhiteNoise(clean.Length, new Random(13)), power, 15)));
            return
            [
                new("pink", clean, Add(clean, pink)),
                new("babble", clean, Add(clean, babble)),
                new("reverb", clean, reverberant),
            ];
        }

        static float[] ReadPcm16(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            stream.Position = 12;
            while (stream.Position < stream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadInt32();
                if (id != "data")
                {
                    stream.Position += size + (size & 1);
                    continue;
                }
                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = reader.ReadInt16() / 32768f;
                return samples;
            }
            throw new InvalidDataException($"{path} に data チャンクがありません。");
        }

        /// <summary>声がある部分（最大の 1/1000 より大きい 10ms の区間）の平均パワー</summary>
        static double ActivePower(float[] signal)
        {
            const int frame = 160;
            var powers = Enumerable.Range(0, signal.Length / frame).Select(f => signal.AsSpan(f * frame, frame).ToArray().Average(v => (double)v * v)).ToArray();
            var threshold = powers.Max() * 1e-3;
            return powers.Where(p => p > threshold).Average();
        }

        /// <summary>声のパワーとの比が snrDb になるよう雑音を拡大・縮小する</summary>
        static float[] Scale(float[] noise, double speechPower, double snrDb)
        {
            var gain = Math.Sqrt(speechPower / Math.Pow(10, snrDb / 10) / noise.Average(v => (double)v * v));
            return [.. noise.Select(v => (float)(v * gain))];
        }

        static float[] Add(float[] a, float[] b) => [.. a.Zip(b, (x, y) => x + y)];

        static float[] WhiteNoise(int length, Random random) => [.. Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 2 - 1))];

        /// <summary>Paul Kellet のフィルターで白色雑音を 1/f にしたもの</summary>
        static float[] PinkNoise(int length, Random random)
        {
            var result = new float[length];
            double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
            for (var i = 0; i < length; i++)
            {
                var white = random.NextDouble() * 2 - 1;
                b0 = 0.99886 * b0 + white * 0.0555179;
                b1 = 0.99332 * b1 + white * 0.0750759;
                b2 = 0.96900 * b2 + white * 0.1538520;
                b3 = 0.86650 * b3 + white * 0.3104856;
                b4 = 0.55000 * b4 + white * 0.5329522;
                b5 = -0.7616 * b5 - white * 0.0168980;
                result[i] = (float)(b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362);
                b6 = white * 0.115926;
            }
            return result;
        }

        /// <summary>電源の 50Hz とその倍音</summary>
        static float[] Hum(int length, int sampleRate)
            => [.. Enumerable.Range(0, length).Select(i => (float)Enumerable.Range(1, 5).Sum(h => Math.Sin(2 * Math.PI * 50 * h * i / sampleRate) / h))];

        /// <summary>Schroeder の残響（並列のくし形フィルター 4 つと直列の全域通過フィルター 2 つ）。残響時間は約 0.6 秒</summary>
        static float[] Reverb(float[] dry, int sampleRate)
        {
            const double rt60 = 0.6;
            var wet = new double[dry.Length];
            foreach (var ms in new[] { 29.7, 37.1, 41.1, 43.7 })
            {
                var delay = (int)(ms * sampleRate / 1000);
                var gain = Math.Pow(10, -3 * ms / 1000 / rt60);
                var line = new double[dry.Length];
                for (var i = 0; i < dry.Length; i++)
                {
                    line[i] = dry[i] + (i >= delay ? gain * line[i - delay] : 0);
                    wet[i] += line[i] / 4;
                }
            }
            foreach (var ms in new[] { 5.0, 1.7 })
            {
                var delay = (int)(ms * sampleRate / 1000);
                const double gain = 0.7;
                var input = (double[])wet.Clone();
                for (var i = 0; i < wet.Length; i++)
                    wet[i] = -gain * input[i] + (i >= delay ? input[i - delay] + gain * wet[i - delay] : 0);
            }
            var wetPower = wet.Average(v => v * v);
            var dryPower = dry.Average(v => (double)v * v);
            // 残響の成分を直接音より 3dB 小さくする
            var scale = Math.Sqrt(dryPower / wetPower / 2);
            return [.. dry.Select((v, i) => (float)(v + wet[i] * scale))];
        }

        // ---- 指標 ----

        /// <summary>scale-invariant SDR（dB）。推定をきれいな声への射影と残りに分けた比</summary>
        internal static double SiSdr(float[] estimate, float[] reference)
        {
            var length = Math.Min(estimate.Length, reference.Length);
            var meanE = estimate.Take(length).Average(v => (double)v);
            var meanR = reference.Take(length).Average(v => (double)v);
            double dot = 0, energy = 0;
            for (var i = 0; i < length; i++)
            {
                dot += (estimate[i] - meanE) * (reference[i] - meanR);
                energy += (reference[i] - meanR) * (reference[i] - meanR);
            }
            var alpha = dot / energy;
            double target = 0, error = 0;
            for (var i = 0; i < length; i++)
            {
                var t = alpha * (reference[i] - meanR);
                var e = estimate[i] - meanE - t;
                target += t * t;
                error += e * e;
            }
            return 10 * Math.Log10(target / error);
        }

        /// <summary>
        /// 対数スペクトルの距離（dB）。32ms の Hann 窓・8ms ずらしで、8kHz までのビンを比べ、きれいな声の大きいフレーム（最大の -40dB 以上）だけを平均する
        /// </summary>
        internal static double Lsd(float[] estimate, float[] reference, int sampleRate)
        {
            var size = (int)(sampleRate * 0.032);
            var hop = size / 4;
            var bins = Math.Min(size / 2, (int)(8000.0 * size / sampleRate));
            var window = Enumerable.Range(0, size).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size)).ToArray();
            var frames = (Math.Min(estimate.Length, reference.Length) - size) / hop;
            var distances = new double[frames];
            var energies = new double[frames];
            var e = new Complex[size];
            var r = new Complex[size];
            for (var f = 0; f < frames; f++)
            {
                for (var i = 0; i < size; i++)
                {
                    e[i] = estimate[f * hop + i] * window[i];
                    r[i] = reference[f * hop + i] * window[i];
                }
                Fourier.Forward(e, FourierOptions.NoScaling);
                Fourier.Forward(r, FourierOptions.NoScaling);
                double sum = 0, energy = 0;
                for (var k = 1; k <= bins; k++)
                {
                    var pr = r[k].Magnitude * r[k].Magnitude;
                    var diff = 10 * Math.Log10(e[k].Magnitude * e[k].Magnitude + 1e-10) - 10 * Math.Log10(pr + 1e-10);
                    sum += diff * diff;
                    energy += pr;
                }
                distances[f] = Math.Sqrt(sum / bins);
                energies[f] = energy;
            }
            var threshold = energies.Max() * 1e-4;
            return Enumerable.Range(0, frames).Where(f => energies[f] > threshold).Average(f => distances[f]);
        }

        // ---- 出力 ----

        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        static void SaveWav(string name, float[] samples, int sampleRate)
        {
            var directory = Environment.GetEnvironmentVariable("REUSE_EVAL_OUT");
            if (string.IsNullOrEmpty(directory))
                return;
            Directory.CreateDirectory(directory);
            using var writer = new WavWriter(File.Create(Path.Combine(directory, name)), sampleRate, 1);
            writer.Write(samples);
        }

        static void AppendResult(string line)
        {
            var directory = Environment.GetEnvironmentVariable("REUSE_EVAL_OUT");
            if (string.IsNullOrEmpty(directory))
                return;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "results.tsv"), line + Environment.NewLine);
        }

        /// <summary>推論 1 回ごとの時間を記録する</summary>
        sealed class TimedEnhancer(ISpeechEnhancer inner) : ISpeechEnhancer
        {
            public List<double> Seconds { get; } = [];

            public int GetChunkSamples(int sampleRate) => inner.GetChunkSamples(sampleRate);

            public float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, CancellationToken token)
            {
                var watch = Stopwatch.StartNew();
                var result = inner.Enhance(samples, sampleRate, token);
                Seconds.Add(watch.Elapsed.TotalSeconds);
                return result;
            }
        }
    }
}
