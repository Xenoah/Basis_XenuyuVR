using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace jp.lilxyzw.emock.Editor
{
    public static class EmockClipConverter
    {
        public static EmockClip Clone(EmockClip clip)
        {
            return new EmockClip
            {
                blendshapes = clip.blendshapes.Select(b => new EmockBlendShape{
                    skinnedMeshRenderer = b.skinnedMeshRenderer,
                    index = b.index,
                    keys = b.keys.Select(k => new EmockBlendShapeKey{time = k.time, value = k.value}).ToArray()
                }).ToArray(),
                transforms = clip.transforms.Select(t => new EmockTransform{
                    transform = t.transform,
                    positions = t.positions.Select(p => new EmockPositionKey{time = p.time, value = p.value}).ToArray(),
                    rotations = t.rotations.Select(r => new EmockRotationKey{time = r.time, value = r.value}).ToArray(),
                    scales = t.scales.Select(s => new EmockScaleKey{time = s.time, value = s.value}).ToArray(),
                }).ToArray(),
                loop = clip.loop,
                start = clip.start,
                end = clip.end,
                fadein = clip.fadein,
                disableBlink = clip.disableBlink,
                disableLipSync = clip.disableLipSync,
                disableEyeTracking = clip.disableEyeTracking
            };
        }

        public static void Optimize(EmockClip clip, bool isDefaultClip = false)
        {
            clip.blendshapes = clip.blendshapes
                .Where(b => {
                    if (!b.skinnedMeshRenderer || !b.skinnedMeshRenderer.sharedMesh || b.index < 0 && b.index >= b.skinnedMeshRenderer.sharedMesh.blendShapeCount) return false;
                    if (isDefaultClip) return true;
                    var value = b.skinnedMeshRenderer.GetBlendShapeWeight(b.index);
                    return b.keys.Any(k => k.value != value);
                })
                .ToArray();

            clip.transforms = clip.transforms
                .Where(t => t.transform)
                .Select(t => new EmockTransform{
                    transform = t.transform,
                    positions = t.positions.Where(p => isDefaultClip || t.positions.Any(p => p.value != t.transform.localPosition)).ToArray(),
                    rotations = t.rotations.Where(r => isDefaultClip || t.rotations.Any(r => r.value != t.transform.localRotation)).ToArray(),
                    scales = t.scales.Where(s => isDefaultClip || t.scales.Any(s => s.value != t.transform.localScale)).ToArray()
                })
                .Where(t => t.positions.Length > 0 || t.rotations.Length > 0 || t.scales.Length > 0)
                .ToArray();
        }

        public static EmockClip GetDefaultClip(EmockClip[] clips)
        {
            var meshes = new Dictionary<SkinnedMeshRenderer, HashSet<int>>();
            var transforms = new Dictionary<Transform, (bool animatePosition, bool animateRotation, bool animateScale)>();
            foreach (var clip in clips)
            {
                foreach (var b in clip.blendshapes)
                {
                    if (!b.skinnedMeshRenderer) continue;
                    if (!meshes.TryGetValue(b.skinnedMeshRenderer, out var list))
                        meshes[b.skinnedMeshRenderer] = list = new();
                    list.Add(b.index);
                }

                foreach (var transform in clip.transforms)
                {
                    if (!transform.transform) continue;
                    transforms.TryGetValue(transform.transform, out var list);
                    list.animatePosition = list.animatePosition || transform.positions.Length > 0;
                    list.animateRotation = list.animateRotation || transform.rotations.Length > 0;
                    list.animateScale = list.animateScale || transform.scales.Length > 0;
                    transforms[transform.transform] = list;
                }
            }
            
            return new()
            {
                blendshapes = meshes.SelectMany(m => m.Value.Select(i => new EmockBlendShape{skinnedMeshRenderer = m.Key, index = i, keys = new EmockBlendShapeKey[]{new(){time = 0, value = m.Key.GetBlendShapeWeight(i)}}})).ToArray(),
                transforms = transforms.Select(t =>
                {
                    var trans = new EmockTransform{transform = t.Key};
                    if (t.Value.animatePosition) trans.positions = new EmockPositionKey[]{new(){time = 0, value = t.Key.localPosition}};
                    if (t.Value.animateRotation) trans.rotations = new EmockRotationKey[]{new(){time = 0, value = t.Key.localRotation}};
                    if (t.Value.animateScale) trans.scales = new EmockScaleKey[]{new(){time = 0, value = t.Key.localScale}};
                    return trans;
                }).ToArray(),
                loop = false,
                start = 0,
                end = 0,
                fadein = clips[1].fadein,
                disableBlink = false,
                disableLipSync = false,
                disableEyeTracking = false
            };
        }

        public static EmockClip FromAnimationClip(AnimationClip clip, GameObject root)
        {
            var blendshapes = new Dictionary<SkinnedMeshRenderer, List<EmockBlendShape>>();
            var positions = new Dictionary<Transform, List<EmockPositionKey>>();
            var eulers = new Dictionary<Transform, List<EulerKey>>();
            var scales = new Dictionary<Transform, List<EmockScaleKey>>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (root.transform.Find(binding.path) is not Transform target) continue;

                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (binding.propertyName.StartsWith("blendShape.") && target.GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer skinnedMeshRenderer && skinnedMeshRenderer.sharedMesh is Mesh mesh)
                {
                    var blendshape = binding.propertyName["blendShape.".Length..];
                    var index = mesh.GetBlendShapeIndex(blendshape);
                    if (index == -1) continue;
                    if (!blendshapes.TryGetValue(skinnedMeshRenderer, out var list))
                        blendshapes[skinnedMeshRenderer] = list = new();

                    list.Add(new(){
                        skinnedMeshRenderer = skinnedMeshRenderer,
                        index = index,
                        keys = curve.keys.Select(k => new EmockBlendShapeKey{time = k.time, value = k.value}).OrderBy(k => k.time).ToArray()
                    });
                    continue;
                }

                if (binding.propertyName == "m_LocalPosition.x")
                {
                    if (!positions.TryGetValue(target, out var list))
                        positions[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EmockPositionKey k) k.value.x = key.value;
                        else list.Add(new(){time = key.time, value = new(key.value,0,0)});
                    }
                    continue;
                }

                if (binding.propertyName == "m_LocalPosition.y")
                {
                    if (!positions.TryGetValue(target, out var list))
                        positions[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EmockPositionKey k) k.value.y = key.value;
                        else list.Add(new(){time = key.time, value = new(0,key.value,0)});
                    }
                    continue;
                }

                if (binding.propertyName == "m_LocalPosition.z")
                {
                    if (!positions.TryGetValue(target, out var list))
                        positions[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EmockPositionKey k) k.value.z = key.value;
                        else list.Add(new(){time = key.time, value = new(0,0,key.value)});
                    }
                    continue;
                }

                if (binding.propertyName == "localEulerAnglesRaw.x")
                {
                    if (!eulers.TryGetValue(target, out var list))
                        eulers[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EulerKey k) k.value.x = key.value;
                        else list.Add(new(){time = key.time, value = new(key.value,0,0)});
                    }
                    continue;
                }

                if (binding.propertyName == "localEulerAnglesRaw.y")
                {
                    if (!eulers.TryGetValue(target, out var list))
                        eulers[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EulerKey k) k.value.y = key.value;
                        else list.Add(new(){time = key.time, value = new(0,key.value,0)});
                    }
                    continue;
                }

                if (binding.propertyName == "localEulerAnglesRaw.z")
                {
                    if (!eulers.TryGetValue(target, out var list))
                        eulers[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EulerKey k) k.value.z = key.value;
                        else list.Add(new(){time = key.time, value = new(0,0,key.value)});
                    }
                    continue;
                }

                if (binding.propertyName == "m_LocalScale.x")
                {
                    if (!scales.TryGetValue(target, out var list))
                        scales[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EmockScaleKey k) k.value.x = key.value;
                        else list.Add(new(){time = key.time, value = new(key.value,0,0)});
                    }
                    continue;
                }

                if (binding.propertyName == "m_LocalScale.y")
                {
                    if (!scales.TryGetValue(target, out var list))
                        scales[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EmockScaleKey k) k.value.y = key.value;
                        else list.Add(new(){time = key.time, value = new(0,key.value,0)});
                    }
                    continue;
                }

                if (binding.propertyName == "m_LocalScale.z")
                {
                    if (!scales.TryGetValue(target, out var list))
                        scales[target] = list = new();
                    foreach (var key in curve.keys)
                    {
                        if (list.FirstOrDefault(r => r.time == key.time) is EmockScaleKey k) k.value.z = key.value;
                        else list.Add(new(){time = key.time, value = new(0,0,key.value)});
                    }
                    continue;
                }
            }

            var rotations = eulers.ToDictionary(e => e.Key, e => e.Value.Select(k => new EmockRotationKey{ time = k.time, value = Quaternion.Euler(k.value)}).ToArray());
            var transforms = new List<EmockTransform>();
            foreach (var transform in positions.Keys.Union(eulers.Keys).Union(scales.Keys).Distinct().ToArray())
            {
                var t = new EmockTransform{transform = transform};
                if (positions.TryGetValue(transform, out var positionKeys)) t.positions = positionKeys.OrderBy(k => k.time).ToArray();
                if (rotations.TryGetValue(transform, out var rotationKeys)) t.rotations = rotationKeys.OrderBy(k => k.time).ToArray();
                if (scales.TryGetValue(transform, out var scaleKeys)) t.scales = scaleKeys.OrderBy(k => k.time).ToArray();
                transforms.Add(t);
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);

            float end = 0;
            if (blendshapes.Any(m => m.Value.Any(b => b.keys.Any()))) end = Math.Max(end, blendshapes.SelectMany(m => m.Value).SelectMany(b => b.keys).Max(k => k.time));
            if (transforms.Any(t => t.positions.Any())) end = Math.Max(end, transforms.SelectMany(t => t.positions).Max(k => k.time));
            if (transforms.Any(t => t.rotations.Any())) end = Math.Max(end, transforms.SelectMany(t => t.rotations).Max(k => k.time));
            if (transforms.Any(t => t.scales.Any())) end = Math.Max(end, transforms.SelectMany(t => t.scales).Max(k => k.time));

            return new(){
                blendshapes = blendshapes.SelectMany(b => b.Value).ToArray(),
                transforms = transforms.ToArray(),
                loop = end != 0 && settings.loopTime,
                start = end != 0 && settings.loopTime ? end * settings.cycleOffset : 0,
                end = end
            };
        }

        private class EulerKey
        {
            public float time;
            public Vector3 value;
        }
    }
}
