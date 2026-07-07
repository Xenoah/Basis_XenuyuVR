using System.Collections.Generic;

namespace jp.lilxyzw.basispatcher
{
    public interface IManagedLateUpdate
    {
        private static List<IManagedLateUpdate> components = new();
        public static void Add(IManagedLateUpdate component) => components.Add(component);
        public static void Remove(IManagedLateUpdate component) => components.Remove(component);
        public static void Invoke()
        {
            foreach (var component in components) component.ManagedLateUpdate();
        }

        public void ManagedLateUpdate();
    }
}
