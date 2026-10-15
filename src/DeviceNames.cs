using System;
using System.IO;
using System.Linq;

namespace OwnDesk
{
    public static class DeviceNames
    {
        public static string Validate(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 48 || name.Any(char.IsControl))
                throw new ArgumentException("电脑名称应为 1 到 48 个普通字符。");
            return name;
        }
        public static string Local(string root)
        {
            string path = Path.Combine(root, "local-name.json");
            if (File.Exists(path))
                try { return Validate(AppInfo.Json().Deserialize<string>(File.ReadAllText(path))); }
                catch (Exception) { }
            return Environment.MachineName;
        }
        public static void SaveLocal(string root, string name)
        {
            AppInfo.AtomicWrite(Path.Combine(root, "local-name.json"), AppInfo.Json().Serialize(Validate(name)));
        }
        public static void Rename(DeviceStore store, Device device, string name)
        {
            var changed = device.Copy(); changed.Name = Validate(name); store.Upsert(changed);
        }
    }
}
