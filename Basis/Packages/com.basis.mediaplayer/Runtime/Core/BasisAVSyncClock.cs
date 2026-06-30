using UnityEngine;

// 実時間の経過を media presentation time へ写像する。
//
// 既定では最初の frame の PTS を anchor にし、
// Time.realtimeSinceStartupAsDouble で進める。audio が無い場合はこれで十分。
//
// IBasisMediaClockSource が接続されている場合 (BasisMediaPlayer は同じ GameObject 上の
// BasisMediaPlayerAudio を自動接続する)、HasMediaTime を報告している間
// CurrentMediaTimeUs はその source に追従する。これにより video presentation は wall time
// ではなく audio playback を追いかける。ffplay / VLC / GStreamer と同じ audio-master pattern で、
// source PTS jitter、loop seam drift、audio hardware clock skew があっても A/V を揃えやすい。
//
// smooth takeover: local wall clock が既に開始済み (audio anchor より先に最初の video frame が来た)
// で、後から external source が HasMediaTime を報告し始めた場合、external の絶対時刻へ
// そのまま切り替えず、local clock の現在値に external delta を anchor する。これにより、
// audio が PTS 0 に anchor される一方で local clock が audio buffer pre-roll 中に約 100ms
// 進んでいたような場合の後退 time jump を避ける。そうしないと queue 済み video frame が
// 永遠に「未来」に残る。
public sealed class BasisAVSyncClock
{
    private bool started;
    private double wallStart;
    private long mediaStartUs;

    private IBasisMediaClockSource externalSource;
    private bool externalAnchorTaken;
    private long externalAnchorMediaUs;
    private long localAtExternalAnchorUs;
    private long lastExternalSampleMediaUs;
    private double lastExternalSampleWallSecs;
    private long lastReportedMediaUs;

    private const double MaxWallInterpolationSecs = 0.5;

    // 設定され、かつ HasMediaTime を報告している間、CurrentMediaTimeUs と IsStarted の両方を駆動する。
    // null に戻すと wall-clock anchoring へ戻る。
    public IBasisMediaClockSource ExternalSource
    {
        get => externalSource;
        set => externalSource = value;
    }

    public bool IsStarted
    {
        get
        {
            if (externalSource != null && externalSource.HasMediaTime) return true;
            return started;
        }
    }

    public long CurrentMediaTimeUs
    {
        get
        {
            long value;
            if (externalSource != null && externalSource.HasMediaTime)
            {
                long externalNow = externalSource.CurrentMediaTimeUs;
                double wallNow = Time.realtimeSinceStartupAsDouble;
                if (!externalAnchorTaken)
                {
                    // local が動いている場合は external delta を local clock の現在値へ固定する。
                    // そうでなければ external の値から素直に開始する。
                    externalAnchorMediaUs = externalNow;
                    localAtExternalAnchorUs = started ? ComputeLocalMediaTimeUs() : externalNow;
                    externalAnchorTaken = true;
                    lastExternalSampleMediaUs = externalNow;
                    lastExternalSampleWallSecs = wallNow;
                }
                else if (externalNow != lastExternalSampleMediaUs)
                {
                    lastExternalSampleMediaUs = externalNow;
                    lastExternalSampleWallSecs = wallNow;
                }

                long baseValue = localAtExternalAnchorUs + (externalNow - externalAnchorMediaUs);
                double wallDeltaSecs = wallNow - lastExternalSampleWallSecs;
                if (wallDeltaSecs < 0) wallDeltaSecs = 0;
                else if (wallDeltaSecs > MaxWallInterpolationSecs) wallDeltaSecs = MaxWallInterpolationSecs;
                value = baseValue + (long)(wallDeltaSecs * 1_000_000.0);
            }
            else if (started)
            {
                value = ComputeLocalMediaTimeUs();
            }
            else
            {
                return 0;
            }

            if (value < lastReportedMediaUs) value = lastReportedMediaUs;
            else lastReportedMediaUs = value;
            return value;
        }
    }

    public void StartAt(long mediaTimeUs)
    {
        wallStart = Time.realtimeSinceStartupAsDouble;
        mediaStartUs = mediaTimeUs;
        started = true;
    }

    public void Reset()
    {
        started = false;
        wallStart = 0;
        mediaStartUs = 0;
        externalAnchorTaken = false;
        externalAnchorMediaUs = 0;
        localAtExternalAnchorUs = 0;
        lastExternalSampleMediaUs = 0;
        lastExternalSampleWallSecs = 0;
        lastReportedMediaUs = 0;
        // ExternalSource は player により一度だけ配線され、Reset() 後も残る。
        // source 自身の anchor は source 側で独立して reset される前提
        // (BasisMediaPlayerAudio.ResetSyncAnchor)。
    }

    private long ComputeLocalMediaTimeUs()
    {
        double elapsedSeconds = Time.realtimeSinceStartupAsDouble - wallStart;
        return mediaStartUs + (long)(elapsedSeconds * 1_000_000.0);
    }
}
