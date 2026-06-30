// 別の何か (通常は audio sink) が駆動する media-time clock を提供する。
// source が未接続なら BasisAVSyncClock は wall-clock pacing へ fallback するが、
// source が存在し HasMediaTime を報告している場合はこちらを優先する。
//
// これは audio-master sync pattern。video presentation は audio system が実際に要求した
// audio sample の時刻へ追従するため、source/network/loop drift に関係なく audio と揃いやすい。
public interface IBasisMediaClockSource
{
    bool HasMediaTime { get; }
    long CurrentMediaTimeUs { get; }
}
