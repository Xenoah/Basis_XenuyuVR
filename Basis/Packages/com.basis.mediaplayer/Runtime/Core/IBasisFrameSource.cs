using System;

// decode 済み video/audio frame の producer。push-based で、implementation は
// read/decode を行う thread から event を発火する。consumer (BasisMediaPlayer) は
// Unity API 呼び出しのため main thread へ marshal する。
//
// implementation: BasisSyntheticTestSource (CPU test pattern)。
// live OS-codec playback は BasisNativeVideoSource (zero-copy GPU) を使い、
// BasisMediaPlayer がこの interface 経由ではなく直接駆動する。
public interface IBasisFrameSource : IDisposable
{
    event Action<BasisVideoFrame> OnVideoFrame;
    event Action<Exception> OnError;
    event Action OnEndOfStream;

    // Start() cycle ごとに一度、source が frame を供給できると確認した後に発火する。
    // live source では「transport 接続済み、かつ最初の packet を観測済み」。
    // seekable source では IBasisSeekableFrameSource.OnPrepared と同じ信号
    // (implementation が転送してよい)。
    event Action OnReady;

    // source が報告する video dimension が変わったときに発火する
    // (初期 resolution、stream 中 resize、adaptive variant switch)。
    // 引数順は (width, height)。player は最新値を cache し、BasisMediaPlayer.VideoSize として公開する。
    event Action<int, int> OnVideoSizeChanged;

    bool IsRunning { get; }

    void Start();
    void Stop();
}
