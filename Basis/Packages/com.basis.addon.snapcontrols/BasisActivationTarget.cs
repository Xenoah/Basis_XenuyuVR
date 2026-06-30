using System;
using UnityEngine;
using UnityEngine.Events;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// 離散状態を持つ任意の interactable 向けの汎用 on/off 接続点。
    /// toggle 対象の GameObject list と、activate/deactivate 時に発火する
    /// UnityEvent pair を任意で保持する。遷移は <see cref="Activate"/> /
    /// <see cref="Deactivate"/> で駆動し、無音の初期 setup には
    /// <see cref="ApplyActiveState"/> を <c>fireEvents = false</c> で使う。
    ///
    /// 呼び出し例: 現在の snap point を activate する snap-path interactable、
    /// 現在の detent を activate する multi-position lever、focus 中の item を
    /// activate する radial-menu selector。
    /// </summary>
    public class BasisActivationTarget : MonoBehaviour
    {
        [Tooltip("GameObjects toggled active=true when this target is activated, active=false when deactivated.")]
        public GameObject[] enableWhileActive = Array.Empty<GameObject>();

        [Tooltip("Fired on activation. Not fired by ApplyActiveState(_, fireEvents: false).")]
        public UnityEvent OnActivated;

        [Tooltip("Fired on deactivation. Not fired by ApplyActiveState(_, fireEvents: false).")]
        public UnityEvent OnDeactivated;

        public bool IsActive { get; private set; }

        public void Activate() => ApplyActiveState(true, fireEvents: true);
        public void Deactivate() => ApplyActiveState(false, fireEvents: true);

        public void ApplyActiveState(bool active, bool fireEvents = true)
        {
            IsActive = active;
            int n = enableWhileActive.Length;
            for (int i = 0; i < n; i++)
            {
                GameObject go = enableWhileActive[i];
                if (go != null) go.SetActive(active);
            }
            if (!fireEvents) return;
            if (active) OnActivated?.Invoke();
            else OnDeactivated?.Invoke();
        }
    }
}
