using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace jp.lilxyzw.emock
{
    public partial class EmockInterpolator
    {
        private Dictionary<EmockClip, BlendShapeJobHolder> blendshapeJobDictionary = new();
        private BlendShapeJobHolder blendshapeJob;
        private BlendShapeFadeJob blendshapeFadeJob;
        private (SkinnedMeshRenderer skinnedMeshRenderer, int index)[] shapes;
        private NativeParallelMultiHashMap<int, (float time, float value)> keysEmpty = new(0, Allocator.Persistent);

        private void DisposeBlendShapes()
        {
            foreach (var kv in blendshapeJobDictionary) kv.Value.job.Dispose();
            blendshapeFadeJob.Dispose();
            keysEmpty.Dispose();
        }

        private void InitializeBlendShapes(EmockClip clip)
        {
            shapes = clip.blendshapes.Select(b => (b.skinnedMeshRenderer, b.index)).ToArray();
            blendshapeFadeJob = new()
            {
                size = clip.blendshapes.Length,
                indicesMap = new(new int[clip.blendshapes.Length], Allocator.Persistent),
                defaults = new(clip.blendshapes.Select(b => b.keys.Length > 0 ? b.keys[0].value : default).ToArray(), Allocator.Persistent),
                previous = new(clip.blendshapes.Select(b => b.keys.Length > 0 ? b.keys[0].value : default).ToArray(), Allocator.Persistent),
                keys = keysEmpty,
                results = new(clip.blendshapes.Length, Allocator.Persistent)
            };
        }

        private void SetPreviousBlendShapes(EmockClip clip)
        {
            for (int i = 0; i < blendshapeFadeJob.previous.Length; i++)
                blendshapeFadeJob.previous[i] = clip.blendshapes[i].skinnedMeshRenderer.GetBlendShapeWeight(clip.blendshapes[i].index);
        }

        private void SetClipBlendShapes(EmockClip clip)
        {
            if (!blendshapeJobDictionary.TryGetValue(clip, out blendshapeJob))
            {
                blendshapeJob = new (){targets = clip.blendshapes.Select(b => (b.skinnedMeshRenderer, b.index)).ToArray(), job = new()
                {
                    size = clip.blendshapes.Length,
                    keys = new(clip.blendshapes.Sum(b => b.keys.Length), Allocator.Persistent),
                    results = new(clip.blendshapes.Length, Allocator.Persistent)
                }};

                for (int i = 0; i < clip.blendshapes.Length; i++)
                {
                    for (int j = 0; j < clip.blendshapes[i].keys.Length; j++)
                        blendshapeJob.job.keys.Add(i, (clip.blendshapes[i].keys[j].time, clip.blendshapes[i].keys[j].value));
                }
            }

            for (int i = 0; i < shapes.Length; i++)
            {
                var target =  shapes[i];

                EmockBlendShape blendshape = null;
                int index = 0;
                for (; index < clip.blendshapes.Length; index++)
                {
                    if (clip.blendshapes[index].skinnedMeshRenderer == target.skinnedMeshRenderer && clip.blendshapes[index].index == target.index)
                    {
                        blendshape = clip.blendshapes[index];
                        break;
                    }
                }

                if (blendshape == null)
                {
                    blendshapeFadeJob.indicesMap[i] = -1;
                }
                else
                {
                    blendshapeFadeJob.indicesMap[i] = index;
                }
            }

            blendshapeFadeJob.keys = blendshapeJob.job.keys;
        }

        private void InterpolateBlendShapesFade(float time, float weight)
        {
            blendshapeFadeJob.time = time;
            blendshapeFadeJob.weight = weight;
            var handle = blendshapeFadeJob.Schedule(blendshapeFadeJob.size, 0);
            handle.Complete();

            for (int i = 0; i < blendshapeFadeJob.size; i++)
            {
                shapes[i].skinnedMeshRenderer.SetBlendShapeWeight(shapes[i].index, blendshapeFadeJob.results[i]);
            }
        }

        private void InterpolateBlendShapes(float time)
        {
            blendshapeJob.job.time = time;
            var handle = blendshapeJob.job.Schedule(blendshapeJob.job.size, 0);
            handle.Complete();

            for (int i = 0; i < blendshapeJob.job.size; i++)
            {
                blendshapeJob.targets[i].skinnedMeshRenderer.SetBlendShapeWeight(blendshapeJob.targets[i].index, blendshapeJob.job.results[i]);
            }
        }

        private class BlendShapeJobHolder
        {
            public (SkinnedMeshRenderer skinnedMeshRenderer, int index)[] targets;
            public BlendShapeJob job;
        }

        [BurstCompile]
        private struct BlendShapeJob : IJobParallelFor, IDisposable
        {
            [ReadOnly] public float time;
            [ReadOnly] public int size;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, float value)> keys;
            public NativeArray<float> results;

            public void Dispose()
            {
                keys.Dispose();
                results.Dispose();
            }

            public void Execute(int index)
            {
                (float time, float value) current = (float.MinValue,default);
                (float time, float value) next = (float.MaxValue,default);
                if (keys.TryGetFirstValue(index, out var value, out var it))
                {
                    if (value.time >= time)
                    {
                        next.time = value.time;
                        next.value = value.value;
                    }
                    else
                    {
                        current.time = value.time;
                        current.value = value.value;
                    }

                    while (keys.TryGetNextValue(out value, ref it))
                    {
                        if (value.time >= time && value.time < next.time)
                        {
                            next.time = value.time;
                            next.value = value.value;
                        }
                        else if (value.time < time && value.time > current.time)
                        {
                            current.time = value.time;
                            current.value = value.value;
                        }
                    }
                    if (current.time == float.MinValue && next.time != float.MaxValue) results[index] = next.value;
                    else if (current.time != float.MinValue && next.time == float.MaxValue) results[index] = current.value;
                    else results[index] = EmockMath.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                }
            }
        }

        [BurstCompile]
        private struct BlendShapeFadeJob : IJobParallelFor, IDisposable
        {
            [ReadOnly] public float time;
            [ReadOnly] public float weight;
            [ReadOnly] public int size;
            [ReadOnly] public NativeArray<int> indicesMap;
            [ReadOnly] public NativeArray<float> defaults;
            [ReadOnly] public NativeArray<float> previous;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, float value)> keys;
            public NativeArray<float> results;

            public void Dispose()
            {
                indicesMap.Dispose();
                defaults.Dispose();
                previous.Dispose();
                // keys.Dispose(); // Shared with BlendShapeJob
                results.Dispose();
            }

            public void Execute(int index)
            {
                // Initialize with the prefab value
                results[index] = defaults[index];

                // Keyframe interpolation
                if (indicesMap[index] != -1 && keys.TryGetFirstValue(indicesMap[index], out var value, out var it))
                {
                    (float time, float value) current = (float.MinValue,default);
                    (float time, float value) next = (float.MaxValue,default);

                    if (value.time >= time)
                    {
                        next.time = value.time;
                        next.value = value.value;
                    }
                    else
                    {
                        current.time = value.time;
                        current.value = value.value;
                    }

                    while (keys.TryGetNextValue(out value, ref it))
                    {
                        if (value.time >= time && value.time < next.time)
                        {
                            next.time = value.time;
                            next.value = value.value;
                        }
                        else if (value.time < time && value.time > current.time)
                        {
                            current.time = value.time;
                            current.value = value.value;
                        }
                    }
                    if (current.time == float.MinValue && next.time != float.MaxValue) results[index] = next.value;
                    else if (current.time != float.MinValue && next.time == float.MaxValue) results[index] = current.value;
                    else results[index] = EmockMath.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                }

                // Fade in
                results[index] = EmockMath.Lerp(results[index], previous[index], weight);
            }
        }
    }
}
