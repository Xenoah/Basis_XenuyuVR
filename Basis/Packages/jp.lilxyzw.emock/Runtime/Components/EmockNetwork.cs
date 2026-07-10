using System;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Networking.Behaviour;
using UnityEngine;

namespace jp.lilxyzw.emock
{
    // Keyframes are not sorted at runtime. Please sort them in the editor before building the avatar.
    // Blend shape indices are also calculated prior to the build.
    [AddComponentMenu("Emock/Emock Network")]
    public class EmockNetwork : BasisNetworkAvatarBehaviour, IEmockAvatarLoad
    {
        public EmockAnimator animator;
        private bool isOwner = false;
        private ushort linkedPlayerID;

        // Call this from external components
        public void SetIndex(ushort i)
        {
            if (!isOwner) return;
            if (i >= animator.clips.Length) i = 0;
            if (IsInitialized) NetworkMessageSend(BitConverter.GetBytes(i), DeliveryMethod.ReliableSequenced);
            animator.Index = i;
        }

        private void Awake()
        {
            #if UNITY_EDITOR
            isOwner = true;
            if (Application.platform == RuntimePlatform.WindowsEditor ||
                Application.platform == RuntimePlatform.LinuxEditor ||
                Application.platform == RuntimePlatform.OSXEditor) return;
            #endif

            if (!animator)
            {
                Debug.LogError("EmockNetwork: Animator not found.");
                Destroy(this);
                return;
            }
        }

        public void OnAvatarReady(BasisAvatar avatar, bool IsOwner)
        {
            isOwner = IsOwner;
            linkedPlayerID = avatar.LinkedPlayerID;
        }

        // Network
        public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            if (isOwner || RemoteUser != linkedPlayerID || buffer == null || buffer.Length < 2) return;
            animator.Index = BitConverter.ToUInt16(buffer, 0);
        }
    }
}
