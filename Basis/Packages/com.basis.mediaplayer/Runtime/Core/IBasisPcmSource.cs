// Unity audio thread から読まれる、pull-based の interleaved float PCM source。
//
// OS-codec engine (BasisNativeVideoSource) は audio を native decode し、ring として公開する。
// BasisMediaPlayerAudio はこれを NativePcmSource として設定し、audio thread 上で pull し、
// interleaved sample を output AudioSource 群へ分割/downmix する。
public interface IBasisPcmSource
{
    // 判明済みの stream audio format。最初の audio frame が decode されるまでは false を返す。
    // それまで sink は無音のまま。
    bool TryGetPcmFormat(out int sampleRate, out int channels);

    // 最大 buffer.Length 個の interleaved float sample で `buffer` を埋め、
    // 書き込んだ float 数を返す。残りは caller が zero-fill する。
    // block してはならず、audio thread から呼び出して安全である必要がある。
    int ReadPcm(float[] buffer);
}
