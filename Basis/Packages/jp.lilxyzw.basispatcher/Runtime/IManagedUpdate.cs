using System.Collections.Generic;

namespace jp.lilxyzw.basispatcher
{
    public interface IManagedUpdate
    {
        private static List<IManagedUpdate> components = new();
        public static void Add(IManagedUpdate component) => components.Add(component);
        public static void Remove(IManagedUpdate component) => components.Remove(component);
        public static void Invoke()
        {
            foreach (var component in components) component.ManagedUpdate();
        }

        public void ManagedUpdate();
    }
}
