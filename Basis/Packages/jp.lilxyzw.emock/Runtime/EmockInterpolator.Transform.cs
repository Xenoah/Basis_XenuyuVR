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
        private Dictionary<EmockClip, TransformJobHolder> transformJobDictionary = new();
        private TransformJobHolder transformJob;
        private TransformFadeJob transformFadeJob;
        private Transform[] transforms;
        private NativeParallelMultiHashMap<int, (float time, Vector3 value)> positionsEmpty = new(0, Allocator.Persistent);
        private NativeParallelMultiHashMap<int, (float time, Quaternion value)> rotationsEmpty = new(0, Allocator.Persistent);
        private NativeParallelMultiHashMap<int, (float time, Vector3 value)> scalesEmpty = new(0, Allocator.Persistent);

        private void DisposeTransforms()
        {
            foreach (var kv in transformJobDictionary) kv.Value.job.Dispose();
            transformFadeJob.Dispose();
            positionsEmpty.Dispose();
            rotationsEmpty.Dispose();
            scalesEmpty.Dispose();
        }

        private void InitializeTransforms(EmockClip clip)
        {
            transforms = clip.transforms.Select(t => t.transform).ToArray();
            transformFadeJob = new()
            {
                size = clip.transforms.Length,
                indicesMap = new(new int[clip.transforms.Length], Allocator.Persistent),
                flags = new(clip.transforms.Select(t => (t.positions.Length > 0, t.rotations.Length > 0, t.scales.Length > 0)).ToArray(), Allocator.Persistent),
                defaultPositions = new(clip.transforms.Select(t => t.positions.Length > 0 ? t.positions[0].value : default).ToArray(), Allocator.Persistent),
                previousPositions = new(clip.transforms.Select(t => t.positions.Length > 0 ? t.positions[0].value : default).ToArray(), Allocator.Persistent),
                defaultRotations = new(clip.transforms.Select(t => t.rotations.Length > 0 ? t.rotations[0].value : default).ToArray(), Allocator.Persistent),
                previousRotations = new(clip.transforms.Select(t => t.rotations.Length > 0 ? t.rotations[0].value : default).ToArray(), Allocator.Persistent),
                defaultScales = new(clip.transforms.Select(t => t.scales.Length > 0 ? t.scales[0].value : default).ToArray(), Allocator.Persistent),
                previousScales = new(clip.transforms.Select(t => t.scales.Length > 0 ? t.scales[0].value : default).ToArray(), Allocator.Persistent),
                positions = positionsEmpty,
                rotations = rotationsEmpty,
                scales = scalesEmpty,
                positionResults = new(clip.transforms.Length, Allocator.Persistent),
                rotationResults = new(clip.transforms.Length, Allocator.Persistent),
                scaleResults = new(clip.transforms.Length, Allocator.Persistent)
            };
        }

        private void SetPreviousTransforms(EmockClip clip)
        {
            for (int i = 0; i < transformFadeJob.previousPositions.Length; i++)
                transformFadeJob.previousPositions[i] = clip.transforms[i].transform.localPosition;

            for (int i = 0; i < transformFadeJob.previousRotations.Length; i++)
                transformFadeJob.previousRotations[i] = clip.transforms[i].transform.localRotation;

            for (int i = 0; i < transformFadeJob.previousScales.Length; i++)
                transformFadeJob.previousScales[i] = clip.transforms[i].transform.localScale;
        }

        private void SetClipTransforms(EmockClip clip)
        {
            if (!transformJobDictionary.TryGetValue(clip, out transformJob))
            {
                transformJob = new (){transforms = clip.transforms.Select(t => t.transform).ToArray(), job = new()
                {
                    size = clip.transforms.Length,
                    flags = new(clip.transforms.Select(t => (t.positions.Length > 0, t.rotations.Length > 0, t.scales.Length > 0)).ToArray(), Allocator.Persistent),
                    positions = new(clip.transforms.Sum(t => t.positions.Length), Allocator.Persistent),
                    rotations = new(clip.transforms.Sum(t => t.rotations.Length), Allocator.Persistent),
                    scales = new(clip.transforms.Sum(t => t.scales.Length), Allocator.Persistent),
                    positionResults = new(clip.transforms.Length, Allocator.Persistent),
                    rotationResults = new(clip.transforms.Length, Allocator.Persistent),
                    scaleResults = new(clip.transforms.Length, Allocator.Persistent),
                }};

                for (int i = 0; i < clip.transforms.Length; i++)
                {
                    for (int j = 0; j < clip.transforms[i].positions.Length; j++)
                        transformJob.job.positions.Add(i, (clip.transforms[i].positions[j].time, clip.transforms[i].positions[j].value));

                    for (int j = 0; j < clip.transforms[i].rotations.Length; j++)
                        transformJob.job.rotations.Add(i, (clip.transforms[i].rotations[j].time, clip.transforms[i].rotations[j].value));

                    for (int j = 0; j < clip.transforms[i].scales.Length; j++)
                        transformJob.job.scales.Add(i, (clip.transforms[i].scales[j].time, clip.transforms[i].scales[j].value));
                }
            }

            for (int i = 0; i < transforms.Length; i++)
            {
                var target =  transforms[i];

                EmockTransform transform = null;
                int transformIndex = 0;
                for (; transformIndex < clip.transforms.Length; transformIndex++)
                {
                    if (clip.transforms[transformIndex].transform == target)
                    {
                        transform = clip.transforms[transformIndex];
                        break;
                    }
                }

                if (transform == null)
                {
                    transformFadeJob.indicesMap[i] = -1;
                }
                else
                {
                    transformFadeJob.indicesMap[i] = transformIndex;
                }
            }

            transformFadeJob.positions = transformJob.job.positions;
            transformFadeJob.rotations = transformJob.job.rotations;
            transformFadeJob.scales = transformJob.job.scales;
        }

        private void InterpolateTransformsFade(float time, float weight)
        {
            transformFadeJob.time = time;
            transformFadeJob.weight = weight;
            var handle = transformFadeJob.Schedule(transformFadeJob.size, 0);
            handle.Complete();

            for (int j = 0; j < transformFadeJob.size; j++)
            {
                if (transformFadeJob.flags[j].hasPosition) transforms[j].localPosition = transformFadeJob.positionResults[j];
                if (transformFadeJob.flags[j].hasRotation) transforms[j].localRotation = transformFadeJob.rotationResults[j];
                if (transformFadeJob.flags[j].hasScale) transforms[j].localScale = transformFadeJob.scaleResults[j];
            }
        }

        private void InterpolateTransforms(float time)
        {
            transformJob.job.time = time;
            var handle = transformJob.job.Schedule(transformJob.job.size, 0);
            handle.Complete();

            for (int j = 0; j < transformJob.job.size; j++)
            {
                if (transformJob.job.flags[j].hasPosition) transformJob.transforms[j].localPosition = transformJob.job.positionResults[j];
                if (transformJob.job.flags[j].hasRotation) transformJob.transforms[j].localRotation = transformJob.job.rotationResults[j];
                if (transformJob.job.flags[j].hasScale) transformJob.transforms[j].localScale = transformJob.job.scaleResults[j];
            }
        }

        private class TransformJobHolder
        {
            public Transform[] transforms;
            public TransformJob job;
        }

        [BurstCompile]
        public struct TransformJob : IJobParallelFor, IDisposable
        {
            [ReadOnly] public float time;
            [ReadOnly] public int size;
            [ReadOnly] public NativeArray<(bool hasPosition, bool hasRotation, bool hasScale)> flags;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, Vector3 value)> positions;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, Quaternion value)> rotations;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, Vector3 value)> scales;

            public NativeArray<Vector3> positionResults;
            public NativeArray<Quaternion> rotationResults;
            public NativeArray<Vector3> scaleResults;

            public void Dispose()
            {
                flags.Dispose();
                positions.Dispose();
                rotations.Dispose();
                scales.Dispose();
                positionResults.Dispose();
                rotationResults.Dispose();
                scaleResults.Dispose();
            }

            public void Execute(int index)
            {
                if (flags[index].hasPosition)
                {
                    // Keyframe interpolation
                    if (positions.TryGetFirstValue(index, out var value, out var it))
                    {
                        (float time, Vector3 value) current = (float.MinValue,default);
                        (float time, Vector3 value) next = (float.MaxValue,default);

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

                        while (positions.TryGetNextValue(out value, ref it))
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
                        if (current.time == float.MinValue && next.time != float.MaxValue) positionResults[index] = next.value;
                        else if (current.time != float.MinValue && next.time == float.MaxValue) positionResults[index] = current.value;
                        else positionResults[index] = Vector3.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                    }
                }

                if (flags[index].hasRotation)
                {
                    // Keyframe interpolation
                    if (rotations.TryGetFirstValue(index, out var value, out var it))
                    {
                        (float time, Quaternion value) current = (float.MinValue,default);
                        (float time, Quaternion value) next = (float.MaxValue,default);

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

                        while (rotations.TryGetNextValue(out value, ref it))
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
                        if (current.time == float.MinValue && next.time != float.MaxValue) rotationResults[index] = next.value;
                        else if (current.time != float.MinValue && next.time == float.MaxValue) rotationResults[index] = current.value;
                        else rotationResults[index] = Quaternion.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                    }
                }

                if (flags[index].hasScale)
                {
                    // Keyframe interpolation
                    if (scales.TryGetFirstValue(index, out var value, out var it))
                    {
                        (float time, Vector3 value) current = (float.MinValue,default);
                        (float time, Vector3 value) next = (float.MaxValue,default);

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

                        while (scales.TryGetNextValue(out value, ref it))
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
                        if (current.time == float.MinValue && next.time != float.MaxValue) scaleResults[index] = next.value;
                        else if (current.time != float.MinValue && next.time == float.MaxValue) scaleResults[index] = current.value;
                        else scaleResults[index] = Vector3.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                    }
                }
            }
        }

        [BurstCompile]
        public struct TransformFadeJob : IJobParallelFor, IDisposable
        {
            [ReadOnly] public float time;
            [ReadOnly] public float weight;
            [ReadOnly] public int size;
            [ReadOnly] public NativeArray<int> indicesMap;
            [ReadOnly] public NativeArray<(bool hasPosition, bool hasRotation, bool hasScale)> flags;
            [ReadOnly] public NativeArray<Vector3> defaultPositions;
            [ReadOnly] public NativeArray<Vector3> previousPositions;
            [ReadOnly] public NativeArray<Quaternion> defaultRotations;
            [ReadOnly] public NativeArray<Quaternion> previousRotations;
            [ReadOnly] public NativeArray<Vector3> defaultScales;
            [ReadOnly] public NativeArray<Vector3> previousScales;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, Vector3 value)> positions;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, Quaternion value)> rotations;
            [ReadOnly] public NativeParallelMultiHashMap<int, (float time, Vector3 value)> scales;

            public NativeArray<Vector3> positionResults;
            public NativeArray<Quaternion> rotationResults;
            public NativeArray<Vector3> scaleResults;

            public void Dispose()
            {
                indicesMap.Dispose();
                flags.Dispose();
                defaultPositions.Dispose();
                previousPositions.Dispose();
                defaultRotations.Dispose();
                previousRotations.Dispose();
                defaultScales.Dispose();
                previousScales.Dispose();
                // Shared with TransformJob
                //positions.Dispose();
                //rotations.Dispose();
                //scales.Dispose();
                positionResults.Dispose();
                rotationResults.Dispose();
                scaleResults.Dispose();
            }

            public void Execute(int index)
            {
                if (flags[index].hasPosition)
                {
                    // Initialize with the prefab value
                    positionResults[index] = defaultPositions[index];

                    // Keyframe interpolation
                    if (indicesMap[index] != -1 && positions.TryGetFirstValue(indicesMap[index], out var value, out var it))
                    {
                        (float time, Vector3 value) current = (float.MinValue,default);
                        (float time, Vector3 value) next = (float.MaxValue,default);

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

                        while (positions.TryGetNextValue(out value, ref it))
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
                        if (current.time == float.MinValue && next.time != float.MaxValue) positionResults[index] = next.value;
                        else if (current.time != float.MinValue && next.time == float.MaxValue) positionResults[index] = current.value;
                        else positionResults[index] = Vector3.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                    }

                    // Fade in
                    positionResults[index] = Vector3.Lerp(positionResults[index], previousPositions[index], weight);
                }

                if (flags[index].hasRotation)
                {
                    // Initialize with the prefab value
                    rotationResults[index] = defaultRotations[index];

                    // Keyframe interpolation
                    if (indicesMap[index] != -1 && rotations.TryGetFirstValue(indicesMap[index], out var value, out var it))
                    {
                        (float time, Quaternion value) current = (float.MinValue,default);
                        (float time, Quaternion value) next = (float.MaxValue,default);

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

                        while (rotations.TryGetNextValue(out value, ref it))
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
                        if (current.time == float.MinValue && next.time != float.MaxValue) rotationResults[index] = next.value;
                        else if (current.time != float.MinValue && next.time == float.MaxValue) rotationResults[index] = current.value;
                        else rotationResults[index] = Quaternion.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                    }

                    // Fade in
                    rotationResults[index] = Quaternion.Lerp(rotationResults[index], previousRotations[index], weight);
                }

                if (flags[index].hasScale)
                {
                    // Initialize with the prefab value
                    scaleResults[index] = defaultScales[index];

                    // Keyframe interpolation
                    if (indicesMap[index] != -1 && scales.TryGetFirstValue(indicesMap[index], out var value, out var it))
                    {
                        (float time, Vector3 value) current = (float.MinValue,default);
                        (float time, Vector3 value) next = (float.MaxValue,default);

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

                        while (scales.TryGetNextValue(out value, ref it))
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
                        if (current.time == float.MinValue && next.time != float.MaxValue) scaleResults[index] = next.value;
                        else if (current.time != float.MinValue && next.time == float.MaxValue) scaleResults[index] = current.value;
                        else scaleResults[index] = Vector3.Lerp(current.value, next.value, EmockMath.Smooth((time - current.time) / (next.time - current.time)));
                    }

                    // Fade in
                    scaleResults[index] = Vector3.Lerp(scaleResults[index], previousScales[index], weight);
                }
            }
        }
    }
}
