using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>landmark inference engine (homuler MediaPipe または no-op) の抽象化。</summary>
    public interface IBasisMediaPipeBackend
    {
        bool IsAvailable { get; }
        string BackendName { get; }

        void Initialize(BasisMediaPipeConfig config);
        void SubmitFrame(WebCamTexture frame, double timestampMs);
        bool TryGetLatestResult(out BasisMediaPipeResult result);
        void Shutdown();
    }
}
