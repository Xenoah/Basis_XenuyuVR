using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// knob、lever、switch など、pivot の周囲にある離散 detent 間を動く
    /// 回転 control 用の snap-path interactable。snap point は detent の位置に置き、
    /// interactable はその間を移動しつつ、移動で sweep した角度だけ回転する。
    /// そのため control の向きは pivot 周りの位置に追従する。
    ///
    /// 回転軸は marker の world position 自体から導出する (2 本の chord の cross product)。
    /// pivot transform は回転中心と回転 reference を提供するが、pivot 自身の up-axis が
    /// 特定方向に揃っているとは仮定しない。detent が必要な場所に marker を配置すれば、
    /// swing axis はそれに追従する。
    ///
    /// index selection は完全に world space で行う:
    /// * interactable から <see cref="BasisInteractableObject.GrabRadius"/> x 2 以内に
    ///   bone がある手は、bone position に最も近い marker を選択する。
    /// * それ以外 (hand laser、desktop center-eye) では、input の
    ///   <see cref="BasisInput.RaycastCoord"/> ray への垂直距離が最小の marker を選ぶ。
    /// </summary>
    public class BasisRotarySnapInteractable : BasisSnapPathInteractable
    {
        [Header("Rotation")]
        [Tooltip("Rotational centre the interactable swings around. The interactable's pose relative to the pivot is captured at Awake; on each snap, that resting pose is re-applied with an additional rotation around the marker-derived swing axis equal to the angle from the resting marker to the current one. Leave empty to translate between markers without applying rotation.")]
        public Transform pivot;

        private Vector3 _restingPositionOffset;
        private Quaternion _restingLocalRotation;
        private Vector3 _arcAxisWorld;
        private int _restingIndex;
        private bool _arcReady;

        public override void Awake()
        {
            base.Awake();
            if (snapPoints == null || snapPoints.Length == 0) return;
            if (CurrentIndex < 0 || CurrentIndex >= snapPoints.Length) return;
            Transform initial = snapPoints[CurrentIndex];
            if (initial == null) return;

            _restingPositionOffset = transform.position - initial.position;
            _restingLocalRotation = pivot != null
                ? Quaternion.Inverse(pivot.rotation) * transform.rotation
                : transform.rotation;
            _restingIndex = CurrentIndex;
            _arcAxisWorld = ComputeArcAxis();
            _arcReady = true;

            EvaluateAtIndex(CurrentIndex, out Vector3 p, out Quaternion r);
            transform.SetPositionAndRotation(p, r);
        }

        protected override void EvaluateAtIndex(int index, out Vector3 position, out Quaternion rotation)
        {
            if (!_arcReady)
            {
                base.EvaluateAtIndex(index, out position, out rotation);
                return;
            }

            if (pivot == null || _arcAxisWorld.sqrMagnitude < 1e-8f)
            {
                position = snapPoints[index].position + _restingPositionOffset;
                rotation = pivot != null ? pivot.rotation * _restingLocalRotation : _restingLocalRotation;
                return;
            }

            float angularDelta = ComputeArcAngle(_restingIndex, index);
            Quaternion arcRotation = Quaternion.AngleAxis(angularDelta, _arcAxisWorld);
            position = snapPoints[index].position + arcRotation * _restingPositionOffset;
            rotation = arcRotation * pivot.rotation * _restingLocalRotation;
        }

        /// <summary>
        /// marker arc の plane normal。<c>snapPoints[mid] - snapPoints[0]</c> と
        /// <c>snapPoints[last] - snapPoints[mid]</c> という 2 本の chord の cross product から導出する。
        /// 0 から last へ進む向きが、返す axis 周りの正角 (right-hand rule) になるよう、
        /// 必要なら符号を反転する。marker が colinear、または 3 個未満なら
        /// <see cref="Vector3.zero"/> を返す。
        /// </summary>
        private Vector3 ComputeArcAxis()
        {
            if (snapPoints == null) return Vector3.zero;
            int snapCount = snapPoints.Length;
            if (snapCount < 3) return Vector3.zero;
            int last = snapCount - 1;
            int mid = snapCount / 2;
            if (snapPoints[0] == null || snapPoints[mid] == null || snapPoints[last] == null) return Vector3.zero;

            Vector3 v1 = snapPoints[mid].position - snapPoints[0].position;
            Vector3 v2 = snapPoints[last].position - snapPoints[mid].position;
            Vector3 axis = Vector3.Cross(v1, v2);
            if (axis.sqrMagnitude < 1e-8f) return Vector3.zero;
            axis.Normalize();

            if (pivot != null)
            {
                Vector3 pivotPos = pivot.position;
                Vector3 dir0 = Vector3.ProjectOnPlane(snapPoints[0].position - pivotPos, axis);
                Vector3 dirLast = Vector3.ProjectOnPlane(snapPoints[last].position - pivotPos, axis);
                if (dir0.sqrMagnitude > 1e-8f && dirLast.sqrMagnitude > 1e-8f
                    && Vector3.SignedAngle(dir0, dirLast, axis) < 0f)
                {
                    axis = -axis;
                }
            }
            return axis;
        }

        /// <summary>
        /// cached arc axis 周りの signed angle (degrees)。<paramref name="fromIndex"/> の
        /// projected direction から <paramref name="toIndex"/> の direction までを測る。
        /// pivot 自身が動いても回転が正しく保たれるよう、現在の pivot position から測定する。
        /// </summary>
        private float ComputeArcAngle(int fromIndex, int toIndex)
        {
            if (snapPoints[fromIndex] == null || snapPoints[toIndex] == null) return 0f;
            Vector3 pivotPos = pivot.position;
            Vector3 dirFrom = Vector3.ProjectOnPlane(snapPoints[fromIndex].position - pivotPos, _arcAxisWorld);
            Vector3 dirTo = Vector3.ProjectOnPlane(snapPoints[toIndex].position - pivotPos, _arcAxisWorld);
            if (dirFrom.sqrMagnitude < 1e-8f || dirTo.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.SignedAngle(dirFrom, dirTo, _arcAxisWorld);
        }

        protected override int FindNearestIndex(BasisInputWrapper interacting)
        {
            if (!_arcReady) return CurrentIndex;
            if (snapPoints == null) return CurrentIndex;
            int snapCount = snapPoints.Length;
            if (snapCount == 0) return CurrentIndex;

            Vector3 handPos = interacting.BoneControl.OutgoingWorldData.position;
            bool isHandRole = interacting.Role == BasisBoneTrackedRole.LeftHand
                              || interacting.Role == BasisBoneTrackedRole.RightHand;
            float grabReach = GrabRadius * 2f;
            bool directGrab = isHandRole && (handPos - transform.position).sqrMagnitude <= grabReach * grabReach;

            BasisInput source = interacting.Source;
            bool useRay = !directGrab && source != null;
            Vector3 rayOrigin = useRay ? source.RaycastCoord.position : default;
            Vector3 rayDir = useRay
                ? (source.RaycastCoord.rotation * Vector3.forward).normalized
                : default;

            int nearest = CurrentIndex;
            float bestScore = float.MaxValue;
            for (int i = 0; i < snapCount; i++)
            {
                Transform marker = snapPoints[i];
                if (marker == null) continue;
                Vector3 markerPos = marker.position;
                float score;
                if (useRay)
                {
                    Vector3 toMarker = markerPos - rayOrigin;
                    float t = Vector3.Dot(toMarker, rayDir);
                    if (t < 0f) continue;
                    Vector3 closestOnRay = rayOrigin + rayDir * t;
                    score = (markerPos - closestOnRay).sqrMagnitude;
                }
                else
                {
                    score = (markerPos - handPos).sqrMagnitude;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    nearest = i;
                }
            }
            return nearest;
        }
    }
}
