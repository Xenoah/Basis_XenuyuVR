using System;

namespace Basis.MediaPipe
{
    /// <summary>
    /// core package を任意の homuler integration assembly から切り離す。
    /// homuler assembly は startup 時に factory を登録する。存在しない場合、Create()
    /// は no-op backend を返し、この機能を inert に保つ。
    /// </summary>
    public static class BasisMediaPipeBackendRegistry
    {
        private static Func<IBasisMediaPipeBackend> _factory;

        public static bool HasBackend => _factory != null;

        public static void Register(Func<IBasisMediaPipeBackend> factory) => _factory = factory;

        public static IBasisMediaPipeBackend Create() =>
            _factory != null ? _factory() : new BasisMediaPipeNullBackend();
    }
}
