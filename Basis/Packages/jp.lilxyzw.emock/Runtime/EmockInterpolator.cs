using System;
using System.Runtime.CompilerServices;

namespace jp.lilxyzw.emock
{
    public partial class EmockInterpolator : IDisposable
    {
        public bool loop;
        public float start;
        public float end;
        public float fadein;
        public bool disableBlink;
        public bool disableLipSync;
        public bool disableEyeTracking;
        public bool needToFade;

        public void Dispose()
        {
            DisposeBlendShapes();
            DisposeTransforms();
        }

        public EmockInterpolator(EmockClip clip)
        {
            InitializeBlendShapes(clip);
            InitializeTransforms(clip);
            loop = clip.loop;
            start = clip.start;
            end = clip.end;
            fadein = clip.fadein;
            disableBlink = clip.disableBlink;
            disableLipSync = clip.disableLipSync;
            disableEyeTracking = clip.disableEyeTracking;
            needToFade = false;
        }

        public bool Interpolate(float time)
        {
            if (needToFade)
            {
                needToFade = time < fadein;
                var weight = needToFade ? (fadein - time) / fadein : 0;

                if (loop && end > 0 && time > start)
                {
                    time -= start;
                    time -= (float)Math.Floor(time / end) * end;
                    time += start;
                }

                InterpolateBlendShapesFade(time, weight);
                InterpolateTransformsFade(time, weight);
            }
            else
            {
                if (loop && end > 0 && time > start)
                {
                    time -= start;
                    time -= (float)Math.Floor(time / end) * end;
                    time += start;
                }

                InterpolateBlendShapes(time);
                InterpolateTransforms(time);
            }

            // Returns true if no update is required
            return !loop && time >= end && time >= fadein;
        }

        public void SetPrevious(EmockClip clip)
        {
            SetPreviousBlendShapes(clip);
            SetPreviousTransforms(clip);
        }

        public void SetClip(EmockClip clip)
        {
            loop = clip.loop;
            start = clip.start;
            end = clip.end;
            fadein = clip.fadein;
            disableBlink = clip.disableBlink;
            disableLipSync = clip.disableLipSync;
            disableEyeTracking = clip.disableEyeTracking;
            needToFade = true;
            SetClipBlendShapes(clip);
            SetClipTransforms(clip);
        }
    }

    public static class EmockMath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Smooth(float t) => t * t * t * (t * (6f * t - 15f) + 10f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
