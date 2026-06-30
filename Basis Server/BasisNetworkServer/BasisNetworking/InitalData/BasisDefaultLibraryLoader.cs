using BasisNetworking.InitialData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace BasisNetworking.InitialData
{
    public static class BasisDefaultLibraryLoader
    {
        public static List<BasisDefaultLibraryConfiguration> LoadedItems = new List<BasisDefaultLibraryConfiguration>();

        public static void LoadXML(string FolderName)
        {
            try
            {
                string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string newFolderPath = Path.Combine(exeDirectory, FolderName);

                if (!Directory.Exists(newFolderPath))
                {
                    Directory.CreateDirectory(newFolderPath);
                    BNL.Log("Folder created successfully: " + newFolderPath);

                    string exampleFilePath = Path.Combine(newFolderPath, "ExampleAvatardisabled.xml[remove]");
                    File.WriteAllText(exampleFilePath, exampleXml);
                    BNL.Log("Example XML file created at: " + exampleFilePath);
                }

                LoadedItems.Clear();

                BasisDefaultLibraryConfiguration[] configurations = BasisDefaultLibraryConfiguration.LoadAllFromFolder(newFolderPath);
                foreach (BasisDefaultLibraryConfiguration config in configurations)
                {
                    if (string.IsNullOrEmpty(config.Url))
                    {
                        BNL.LogError("Skipping default library entry with empty Url");
                        continue;
                    }
                    LoadedItems.Add(config);
                    BNL.Log($"Default library entry loaded: Mode={config.Mode}, Url={config.Url}");
                }

                BNL.Log($"Default library loaded with {LoadedItems.Count} item(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading default library: {ex.Message}");
            }
        }

        /// <summary>
        /// 単一 entry を設定済み folder 下の新しい XML file として永続化し、
        /// in-memory list に追加する。書き込んだ absolute path を返し、
        /// 失敗時は空文字を返す。
        /// </summary>
        public static string SaveItem(string folderName, BasisDefaultLibraryConfiguration config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.Url))
            {
                BNL.LogError("Refusing to save default library entry with empty Url.");
                return string.Empty;
            }

            try
            {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folderName);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string fileName = BuildUniqueFileName(folder, config);
                string fullPath = Path.Combine(folder, fileName);

                var serializer = new XmlSerializer(typeof(BasisDefaultLibraryConfiguration));
                using (var writer = new StreamWriter(fullPath))
                {
                    serializer.Serialize(writer, config);
                }

                LoadedItems.Add(config);
                BNL.Log($"Default library entry saved: {fullPath}");
                return fullPath;
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to save default library entry: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Url が一致する (case-insensitive) 永続化済み default-library XML をすべて削除し、
        /// in-memory list からも一致 entry を落とす。削除した file 数を返す。
        /// 一致がない場合は 0 (caller 側では成功扱い)。
        /// </summary>
        public static int RemoveItem(string folderName, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                BNL.LogError("Refusing to remove default library entry with empty Url.");
                return 0;
            }

            int removed = 0;
            try
            {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folderName);
                if (Directory.Exists(folder))
                {
                    var serializer = new XmlSerializer(typeof(BasisDefaultLibraryConfiguration));
                    foreach (var file in Directory.GetFiles(folder, "*.xml"))
                    {
                        BasisDefaultLibraryConfiguration config;
                        try
                        {
                            using var reader = new StreamReader(file);
                            config = (BasisDefaultLibraryConfiguration)serializer.Deserialize(reader);
                        }
                        catch (Exception ex)
                        {
                            BNL.LogError($"Skipping unreadable default library file {file}: {ex.Message}");
                            continue;
                        }

                        if (config != null && string.Equals(config.Url, url, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                File.Delete(file);
                                removed++;
                                BNL.Log($"Default library entry removed: {file}");
                            }
                            catch (Exception ex)
                            {
                                BNL.LogError($"Failed to delete default library file {file}: {ex.Message}");
                            }
                        }
                    }
                }

                LoadedItems.RemoveAll(c => c != null && string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to remove default library entry: {ex.Message}");
            }
            return removed;
        }

        private static string BuildUniqueFileName(string folder, BasisDefaultLibraryConfiguration config)
        {
            string modeName = config.Mode switch
            {
                0 => "avatar",
                1 => "world",
                2 => "prop",
                _ => "item",
            };
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            string baseName = $"{modeName}_{stamp}.xml";
            string candidate = Path.Combine(folder, baseName);

            // 防御的措置: millisecond 精度なら衝突はほぼ起きないが、
            // 同じ millisecond 内の 2 回 click では上書きされ得るため counter を付ける。
            int counter = 1;
            while (File.Exists(candidate))
            {
                baseName = $"{modeName}_{stamp}_{counter}.xml";
                candidate = Path.Combine(folder, baseName);
                counter++;
            }
            return baseName;
        }

        public const string exampleXml = @"<BasisDefaultLibraryConfiguration>
    <!-- 0 = Avatar, 1 = World, 2 = Prop -->
    <Mode>0</Mode>
    <!-- Bee file の URL -->
    <Url></Url>
    <!-- ロック解除用 password -->
    <Password></Password>
</BasisDefaultLibraryConfiguration>";
    }
}
