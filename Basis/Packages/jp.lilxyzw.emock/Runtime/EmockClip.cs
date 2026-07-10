using System;
using UnityEngine;

namespace jp.lilxyzw.emock
{
    [Serializable]
    public class EmockClip
    {
        public EmockBlendShape[] blendshapes = {};
        public EmockTransform[] transforms = {};
        public bool loop = false;
        public float start = 0;
        public float end = 0;
        public float fadein = 0;
        public bool disableBlink = false;
        public bool disableLipSync = false;
        public bool disableEyeTracking = false;

        public bool Validate()
        {
            if (loop && end <= 0) loop = false;

            foreach (var s in blendshapes)
            {
                if (!s.skinnedMeshRenderer || !s.skinnedMeshRenderer.sharedMesh || s.index < 0 || s.index >= s.skinnedMeshRenderer.sharedMesh.blendShapeCount) return false;
            }
            foreach (var t in transforms)
            {
                if (!t.transform) return false;
            }
            return true;
        }
    }

    [Serializable]
    public class EmockBlendShape
    {
        public SkinnedMeshRenderer skinnedMeshRenderer;
        public int index;
        public EmockBlendShapeKey[] keys = {};
    }

    [Serializable]
    public class EmockBlendShapeKey
    {
        public float time;
        public float value;
    }

    // Transform
    [Serializable]
    public class EmockTransform
    {
        public Transform transform;
        public EmockPositionKey[] positions = {};
        public EmockRotationKey[] rotations = {};
        public EmockScaleKey[] scales = {};
    }

    [Serializable]
    public class EmockPositionKey
    {
        public float time;
        public Vector3 value;
    }

    [Serializable]
    public class EmockRotationKey
    {
        public float time;
        public Quaternion value;
    }

    [Serializable]
    public class EmockScaleKey
    {
        public float time;
        public Vector3 value;
    }

    public class EmockTransformHolder
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }
}
