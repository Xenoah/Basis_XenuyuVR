using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// 1 つ以上の snap-path interactable で駆動する marker ごとの material highlight。
    /// marker GameObject (いずれかの BasisRotarySnapInteractable の snapPoints array に入る
    /// transform) に付ける。各 source は (interactable, highlight material) pair として設定する。
    /// source の CurrentIndex がこの marker を選んでいる間、highlight を target renderer の
    /// element-0 slot に適用する。highlight 中の source がなければ、Awake で取得した
    /// element-0 material を復元する。
    ///
    /// 複数 source が同じ marker を同時に選ぶ場合がある (例: clock の hour hand と minute hand が
    /// どちらも "12" を指す)。最も最近 activate された source の material が勝ち、
    /// source が外れると stack が空になるまで前の勝者が引き継ぐ。
    /// </summary>
    public class BasisRotarySnapMarkerTint : MonoBehaviour
    {
        [System.Serializable]
        public struct Source
        {
            [Tooltip("Snap-path interactable to watch. This marker's transform must appear in its snapPoints array, otherwise this entry is silently ignored at runtime.")]
            public BasisSnapPathInteractable interactable;

            [Tooltip("Material applied to the renderer's element-0 slot while this interactable selects this marker. If null, no swap happens for this source (the next-priority source or the default takes over).")]
            public Material highlight;
        }

        [Tooltip("Renderer whose element-0 material is swapped. Auto-assigned to GetComponent<Renderer>() at Awake if left null.")]
        public Renderer targetRenderer;

        [Tooltip("Sources that may highlight this marker. The most-recently-activated source wins when multiple are active simultaneously.")]
        public Source[] sources;

        private Material _defaultMaterial;
        private UnityAction<int, int>[] _listeners;
        private int[] _markerIndexInSource;
        private readonly List<int> _activeStack = new List<int>();

        private void Awake()
        {
            if (targetRenderer == null) TryGetComponent(out targetRenderer);
            if (targetRenderer != null) _defaultMaterial = targetRenderer.sharedMaterial;
        }

        private void Start()
        {
            int n = sources != null ? sources.Length : 0;
            _listeners = new UnityAction<int, int>[n];
            _markerIndexInSource = new int[n];
            for (int i = 0; i < n; i++)
            {
                BasisSnapPathInteractable src = sources[i].interactable;
                if (src == null)
                {
                    _markerIndexInSource[i] = -1;
                    continue;
                }
                _markerIndexInSource[i] = IndexOfMarker(src);
                int captured = i;
                _listeners[i] = (previous, target) => HandleSnapChange(captured, previous, target);
                src.OnSnapIndexChanged.AddListener(_listeners[i]);

                if (_markerIndexInSource[i] >= 0 && src.CurrentIndex == _markerIndexInSource[i])
                {
                    Push(i);
                }
            }
            ApplyTopMaterial();
        }

        private void OnDestroy()
        {
            if (_listeners == null) return;
            int n = _listeners.Length;
            for (int i = 0; i < n; i++)
            {
                if (_listeners[i] == null) continue;
                if (sources[i].interactable != null)
                {
                    sources[i].interactable.OnSnapIndexChanged.RemoveListener(_listeners[i]);
                }
            }
        }

        private int IndexOfMarker(BasisSnapPathInteractable source)
        {
            Transform[] pts = source.snapPoints;
            if (pts == null) return -1;
            int n = pts.Length;
            for (int i = 0; i < n; i++)
            {
                if (pts[i] == transform) return i;
            }
            return -1;
        }

        private void HandleSnapChange(int sourceIdx, int previous, int target)
        {
            int my = _markerIndexInSource[sourceIdx];
            if (my < 0) return;
            bool wasActive = previous == my;
            bool isActive = target == my;
            if (wasActive && !isActive) RemoveFromStack(sourceIdx);
            else if (!wasActive && isActive) Push(sourceIdx);
            else return;
            ApplyTopMaterial();
        }

        private void Push(int sourceIdx)
        {
            RemoveFromStack(sourceIdx);
            _activeStack.Add(sourceIdx);
        }

        private void RemoveFromStack(int sourceIdx)
        {
            for (int i = _activeStack.Count - 1; i >= 0; i--)
            {
                if (_activeStack[i] == sourceIdx) { _activeStack.RemoveAt(i); return; }
            }
        }

        private void ApplyTopMaterial()
        {
            if (targetRenderer == null) return;
            Material m = _defaultMaterial;
            if (_activeStack.Count > 0)
            {
                int top = _activeStack[_activeStack.Count - 1];
                if (sources[top].highlight != null) m = sources[top].highlight;
            }
            targetRenderer.sharedMaterial = m;
        }
    }
}
