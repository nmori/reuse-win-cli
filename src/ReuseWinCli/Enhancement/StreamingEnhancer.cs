using System;
using System.Threading;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// ストリーミング推論の窓の組み方（サンプル数）。
    /// 毎回、過去の文脈 Context・新しい区間 Hop・先読み Lookahead をつないだ窓を推論し、新しい区間の分だけを出力する。
    /// 前の窓の出力と、新しい区間の先頭の Crossfade サンプルでなめらかにつなぐ（前の窓の先読みの分を使うので Lookahead 以下）
    /// </summary>
    internal sealed record StreamingSettings(int ContextSamples, int HopSamples, int LookaheadSamples, int CrossfadeSamples)
    {
        public int WindowSamples => ContextSamples + HopSamples + LookaheadSamples;

        public static StreamingSettings FromSeconds(int sampleRate, double context, double hop, double lookahead, double crossfade)
        {
            int Samples(double seconds) => (int)Math.Round(seconds * sampleRate);
            return new(Samples(context), Samples(hop), Samples(lookahead), Samples(crossfade));
        }

        public void Validate()
        {
            if (HopSamples <= 0 || ContextSamples < 0 || LookaheadSamples < 0)
                throw new ArgumentException("ホップは正、文脈と先読みは 0 以上にしてください。");
            if (CrossfadeSamples < 0 || CrossfadeSamples > Math.Min(HopSamples, LookaheadSamples))
                throw new ArgumentException("クロスフェードはホップと先読みより長くできません。");
        }
    }

    /// <summary>
    /// モノラルの音声を少しずつ受け取りながら RE-USE で補正する（ライブの入力向け）。
    /// RE-USE は時間方向に双方向（未来も見る）なので、ホップごとに「過去の文脈・新しい区間・先読み」の窓を推論し直し、新しい区間の分だけを出力する。
    /// 出力は入力と同じ時刻の並びで、入力より Hop + Lookahead サンプル（と 1 回の推論の時間）遅れて出てくる。
    /// 1 つのインスタンスは 1 つのスレッドから使う。チャンネルごとにインスタンスを作れば、推論器は共有してよい
    /// </summary>
    internal sealed class StreamingEnhancer
    {
        readonly ISpeechEnhancer enhancer;
        readonly int sampleRate;
        readonly StreamingSettings settings;
        readonly float[] fade;

        // 入力の保持分。buffer[0] が入力の先頭から数えて bufferStart サンプル目
        float[] buffer = new float[1024];
        int bufferLength;
        long bufferStart;
        long received;
        long emitted;
        float[]? previousTail;

        public StreamingEnhancer(ISpeechEnhancer enhancer, int sampleRate, StreamingSettings settings)
        {
            settings.Validate();
            this.enhancer = enhancer;
            this.sampleRate = sampleRate;
            this.settings = settings;
            fade = new float[settings.CrossfadeSamples];
            for (var i = 0; i < fade.Length; i++)
                fade[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (i + 0.5) / fade.Length));
        }

        /// <summary>入力から出力までの遅れ（推論の時間を除く）</summary>
        public int LatencySamples => settings.HopSamples + settings.LookaheadSamples;

        /// <summary>これまでに出力したサンプル数</summary>
        public long Emitted => emitted;

        /// <summary>入力を足し、出せるようになった区間を補正して返す。返す値は前回の続き（入力の先頭から数えて <see cref="Emitted"/> サンプル目から）</summary>
        public float[] Process(ReadOnlySpan<float> input, CancellationToken token = default)
        {
            Append(input);
            var hop = settings.HopSamples;
            var steps = (int)Math.Max(0, (received - settings.LookaheadSamples - emitted) / hop);
            var output = new float[steps * hop];
            for (var s = 0; s < steps; s++)
                Step(emitted + hop + settings.LookaheadSamples, hop, output.AsSpan(s * hop, hop), token);
            return output;
        }

        /// <summary>入力の終わり。先読みが足りない残りの区間も、ある分だけで補正して返す</summary>
        public float[] Flush(CancellationToken token = default)
        {
            var output = new float[received - emitted];
            var written = 0;
            while (emitted < received)
            {
                var count = (int)Math.Min(settings.HopSamples, received - emitted);
                var end = Math.Min(emitted + settings.HopSamples + settings.LookaheadSamples, received);
                Step(end, count, output.AsSpan(written, count), token);
                written += count;
            }
            return output;
        }

        /// <summary>入力の [end - Window, end) を推論し、[emitted, emitted + count) を destination に書く</summary>
        void Step(long end, int count, Span<float> destination, CancellationToken token)
        {
            var start = Math.Max(0, end - settings.WindowSamples);
            var window = buffer.AsSpan((int)(start - bufferStart), (int)(end - start));
            var enhanced = enhancer.Enhance(window, sampleRate, token);
            var offset = (int)(emitted - start);
            enhanced.AsSpan(offset, count).CopyTo(destination);

            if (previousTail is not null)
            {
                var length = Math.Min(previousTail.Length, count);
                for (var i = 0; i < length; i++)
                    destination[i] = previousTail[i] + fade[i] * (destination[i] - previousTail[i]);
            }
            // 次の区間の先頭に当たる、この窓の先読みの部分を取っておく
            var tail = Math.Min(fade.Length, enhanced.Length - offset - count);
            previousTail = tail > 0 ? enhanced.AsSpan(offset + count, tail).ToArray() : null;

            emitted += count;
            // Flush の窓は先読みが足りない分だけ前にずれるので、次に出す位置の 1 窓分前まで残す
            Trim(emitted - settings.WindowSamples);
        }

        void Append(ReadOnlySpan<float> input)
        {
            if (bufferLength + input.Length > buffer.Length)
                Array.Resize(ref buffer, Math.Max(buffer.Length * 2, bufferLength + input.Length));
            input.CopyTo(buffer.AsSpan(bufferLength));
            bufferLength += input.Length;
            received += input.Length;
        }

        /// <summary>次の窓より前の入力を捨てる</summary>
        void Trim(long keepFrom)
        {
            var drop = (int)Math.Clamp(keepFrom - bufferStart, 0, bufferLength);
            if (drop == 0)
                return;
            buffer.AsSpan(drop, bufferLength - drop).CopyTo(buffer);
            bufferLength -= drop;
            bufferStart += drop;
        }
    }
}
