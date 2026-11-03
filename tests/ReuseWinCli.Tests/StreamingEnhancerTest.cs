using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// ストリーミング推論の窓の切り出しと出力の並びを、GPU を使わない偽の推論で検証する
    /// </summary>
    class StreamingEnhancerTest
    {
        const int SampleRate = 8000;

        /// <summary>サンプルごとに決まる値を返す偽の推論。窓の位置を取り違えると出力がずれる</summary>
        sealed class PointwiseEnhancer : ISpeechEnhancer
        {
            public List<int> Lengths { get; } = [];

            public int GetChunkSamples(int sampleRate) => throw new NotSupportedException();

            public float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, System.Threading.CancellationToken token)
            {
                Lengths.Add(samples.Length);
                var result = new float[samples.Length];
                for (var i = 0; i < samples.Length; i++)
                    result[i] = MathF.Tanh(samples[i] * 0.7f);
                return result;
            }
        }

        [TestCase(1)]
        [TestCase(37)]
        [TestCase(1000)]
        public void OutputMatchesInputPositionsWhateverTheBlockSize(int block)
        {
            var settings = new StreamingSettings(ContextSamples: 300, HopSamples: 80, LookaheadSamples: 50, CrossfadeSamples: 20);
            var fake = new PointwiseEnhancer();
            var streamer = new StreamingEnhancer(fake, SampleRate, settings);
            var random = new Random(3);
            var input = Enumerable.Range(0, 2345).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();

            var output = new List<float>();
            for (var i = 0; i < input.Length; i += block)
            {
                var chunk = input.AsSpan(i, Math.Min(block, input.Length - i));
                output.AddRange(streamer.Process(chunk));
                // 先読みの分が届くまでは出さず、届いた区間はホップ単位で出す
                var received = i + chunk.Length;
                Assert.That(streamer.Emitted, Is.EqualTo(Math.Max(0, received - settings.LookaheadSamples) / settings.HopSamples * settings.HopSamples));
            }
            output.AddRange(streamer.Flush());

            // 偽の推論はサンプルごとの変換なので、窓の位置が正しければ、どの窓のどの位置から取っても（つなぎ目を混ぜても）同じ値になる
            Assert.That(output, Has.Count.EqualTo(input.Length));
            for (var i = 0; i < input.Length; i++)
                Assert.That(output[i], Is.EqualTo(MathF.Tanh(input[i] * 0.7f)).Within(1e-6), $"sample {i}");
            Assert.That(fake.Lengths.Max(), Is.LessThanOrEqualTo(settings.WindowSamples));
        }

        [Test]
        public void LatencyIsHopPlusLookahead()
        {
            var settings = new StreamingSettings(100, 40, 30, 10);
            var streamer = new StreamingEnhancer(new PointwiseEnhancer(), SampleRate, settings);
            Assert.That(streamer.LatencySamples, Is.EqualTo(70));
            Assert.That(streamer.Process(new float[69]), Is.Empty);
            Assert.That(streamer.Process(new float[1]), Has.Length.EqualTo(40));
        }

        [Test]
        public void RejectsCrossfadeLongerThanLookahead()
            => Assert.Throws<ArgumentException>(() => new StreamingEnhancer(new PointwiseEnhancer(), SampleRate, new StreamingSettings(100, 40, 10, 20)));
    }
}
