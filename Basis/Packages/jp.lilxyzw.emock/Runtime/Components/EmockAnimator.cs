using UnityEngine;

using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking;
using HVR.Basis.Comms;
using jp.lilxyzw.basispatcher;

namespace jp.lilxyzw.emock
{
    [AddComponentMenu("Emock/Emock Animator")]
    public class EmockAnimator : MonoBehaviour, IEmockAvatarLoad, IManagedLateUpdate
    {
        public EmockClip[] clips = {};
        private ushort m_Index = 0;
        internal ushort Index
        {
            get
            {
                return m_Index;
            }
            set
            {
                if (value != m_Index && value >= 0 && value < clips.Length)
                {
                    interpolator.SetPrevious(clips[0]);
                    interpolator.SetClip(clips[value]);
                    time = 0;
                    stopped = false;
                    m_Index = value;
                }
            }
        }
        internal float time = 0;
        private bool stopped = true;
        private bool isOwner = false;

        private Transform root;
        private BasisNetworkReceiver receiver;
        private FaceTrackingActivityRelay faceTrackingActivityRelay;
        private EmockInterpolator interpolator;

        private void Awake()
        {
            foreach (var clip in clips)
            {
                if (!clip.Validate())
                {
                    Debug.LogError($"Emock: There are invalid clips.");
                    Destroy(this);
                    return;
                }
            }

            root = transform;

            #if UNITY_EDITOR
            isOwner = true;
            IManagedLateUpdate.Add(this);
            if (Application.platform == RuntimePlatform.WindowsEditor ||
                Application.platform == RuntimePlatform.LinuxEditor ||
                Application.platform == RuntimePlatform.OSXEditor) return;
            #endif

            interpolator = new(clips[0]);

            if (GetComponentInParent<BasisAvatar>(true) is BasisAvatar avatar)
            {
                avatar.OnAvatarReady += IsOwner =>
                {
                    foreach (var component in avatar.GetComponentsInChildren<IEmockAvatarLoad>(true))
                        component.OnAvatarReady(avatar, IsOwner);
                };
            }
            else
            {
                Debug.LogError("Emock: Avatar not found.");
            }
        }

        public void OnAvatarReady(BasisAvatar avatar, bool IsOwner)
        {
            IManagedLateUpdate.Add(this);
            root = avatar.transform;
            faceTrackingActivityRelay = GetComponentInParent<FaceTrackingActivityRelay>(true);
            isOwner = IsOwner;

            if (IsOwner) return;

            if (BasisNetworkPlayers.AvatarToPlayer(avatar, out _, out var networkPlayer))
            {
                receiver = networkPlayer as BasisNetworkReceiver;
            }
            else
            {
                Debug.LogError($"Emock: BasisNetworkReceiver not found. {avatar.name}");
            }
        }

        public void ManagedLateUpdate()
        {
            #if UNITY_EDITOR
            interpolator ??= new(clips[0]);
            #endif

            if (stopped ||
                Vector3.Distance(root.transform.position, BasisLocalPlayer.Instance.transform.position) > EmockSettings.StopDistance.RawValue ||
                faceTrackingActivityRelay && faceTrackingActivityRelay.IsTrackingActive
            ) return;

            stopped = interpolator.Interpolate(time);

            if (time == 0)
            {
                SetBlink(!interpolator.disableBlink);
                SetLipSync(!interpolator.disableLipSync);
                SetEyeTracking(!interpolator.disableEyeTracking);
            }
            time += Time.deltaTime;
        }

        private void OnDestroy()
        {
            SetBlink(true);
            SetLipSync(true);
            SetEyeTracking(true);
            interpolator?.Dispose();
            IManagedLateUpdate.Remove(this);
        }

        private void SetBlink(bool enable)
        {
            if (isOwner)
            {
                BasisLocalPlayer.Instance.FacialBlinkDriver.SetOverride(!enable);
            }
            else if (receiver != null)
            {
                receiver.RemotePlayer.RemoteFaceDriver.OverrideBlinking = !enable;
                if (!enable)
                {
                    for (int i = 0; i < receiver.RemotePlayer.RemoteFaceDriver.blendShapeCount; i++)
                    {
                        receiver.RemotePlayer.RemoteFaceDriver.SafeSetBlendShape(receiver.RemotePlayer.RemoteFaceDriver.blendShapeIndices[i], 0);
                    }
                }
            }
        }

        private void SetLipSync(bool enable)
        {
            if (isOwner)
            {
                //BasisLocalPlayer.Instance.LocalVisemeDriver;
            }
            else if (receiver != null)
            {
                receiver.RemotePlayer.RemoteFaceDriver.OverrideViseme = !enable;
                if (!enable)
                {
                    for (int i = 0; i < receiver.RemotePlayer.RemoteFaceDriver.blendShapeCount; i++)
                    {
                        receiver.RemotePlayer.RemoteFaceDriver.SafeSetBlendShape(receiver.RemotePlayer.RemoteFaceDriver.blendShapeIndices[i], 0);
                    }
                }
            }
        }

        private void SetEyeTracking(bool enable)
        {
            if (isOwner)
            {
                //BasisLocalPlayer.Instance.LocalEyeDriver.;
            }
            else if (receiver != null)
            {
                receiver.RemotePlayer.RemoteFaceDriver.OverrideEye = !enable;
                if (!enable)
                {
                    for (int i = 0; i < receiver.RemotePlayer.RemoteFaceDriver.blendShapeCount; i++)
                    {
                        receiver.RemotePlayer.RemoteFaceDriver.SafeSetBlendShape(receiver.RemotePlayer.RemoteFaceDriver.blendShapeIndices[i], 0);
                    }
                }
            }
        }
    }
}
