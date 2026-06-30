using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    public abstract class BasisVehiclePart : MonoBehaviour
    {
        /// <summary>
        /// この part が取り付く親 vehicle body の Rigidbody。
        /// </summary>
        protected Rigidbody _parentBody = null;

        /// <summary>
        /// この part が取り付く親 vehicle の BasisVehicleBody。
        /// Rigidbody が BasisVehicleBody component と同じ GameObject 上にある場合に設定される。
        /// </summary>
        protected Main.BasisVehicleBody _parentVehicleBody = null;

        /// <summary>
        /// visual effect に使う particle system (あれば)。
        /// </summary>
        protected ParticleSystem _particles = null;
        /// <summary>
        /// particle system の emission module (あれば)。
        /// </summary>
        protected ParticleSystem.EmissionModule _particleEmission;

        /// <summary>
        /// false の場合、この part は回転せず active force (thrust など) も適用しないが、
        /// passive force (lift、drag など) は引き続き適用する。
        /// part を完全に無効化したい場合は、<see cref="UnityEngine.Behaviour.enabled"/> で component を無効化する。
        /// </summary>
        [Tooltip("Whether to apply active forces. Passive forces still apply.")]
        public bool Active = true;

        /// <summary>
        /// vehicle input に基づいて steering と thrust を設定する。
        /// BasisVehiclePart を継承する非 abstract class は、vehicle input の処理を実装する必要がある。
        /// </summary>
        /// <param name="angularInput">vehicle の angular input。範囲は -1.0 から 1.0。</param>
        /// <param name="linearInput">vehicle の linear input。範囲は -1.0 から 1.0。</param>
        public abstract void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput);

        protected virtual void Awake()
        {
            _particles = GetComponent<ParticleSystem>();
            if (_particles == null)
            {
                _particles = GetComponentInChildren<ParticleSystem>();
            }
            if (_particles != null)
            {
                // これは struct だが、正しい扱いは copy すること。
                _particleEmission = _particles.emission;
            }
        }

        protected virtual void OnEnable()
        {
            _parentBody = FindParentRigidbody();
            if (_parentBody != null)
            {
                _parentVehicleBody = _parentBody.GetComponent<Main.BasisVehicleBody>();
                if (_parentVehicleBody != null)
                {
                    _parentVehicleBody.RegisterPart(this);
                }
            }
        }

        private Rigidbody FindParentRigidbody()
        {
            Transform parent = transform.parent;
            while (parent != null)
            {
                Rigidbody rb = parent.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    return rb;
                }
                BasisDebug.LogWarning("BasisVehicleHoverThruster should ideally be a direct child of a GameObject with Rigidbody and BasisVehicleBody components.");
                parent = parent.parent;
            }
            return null;
        }
    }
}
