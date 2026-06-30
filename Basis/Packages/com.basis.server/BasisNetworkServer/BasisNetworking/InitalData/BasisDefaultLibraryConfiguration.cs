using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace BasisNetworking.InitialData
{
    [Serializable]
    public class BasisDefaultLibraryConfiguration
    {
        // client 側の BundledContentHolder.Mode と対応する: 0=Avatar, 1=World, 2=Prop。
        public byte Mode = 0;
        public string Url = "";
        public string Password = "";

        public static BasisDefaultLibraryConfiguration[] LoadAllFromFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException($"The folder '{folderPath}' does not exist.");
            }

            List<BasisDefaultLibraryConfiguration> configurations = new List<BasisDefaultLibraryConfiguration>();

            string[] xmlFiles = Directory.GetFiles(folderPath, "*.xml");
            var serializer = new XmlSerializer(typeof(BasisDefaultLibraryConfiguration));
            foreach (var file in xmlFiles)
            {
                using var reader = new StreamReader(file);
                configurations.Add((BasisDefaultLibraryConfiguration)serializer.Deserialize(reader));
                reader.Close();
            }

            return configurations.ToArray();
        }
    }
}
