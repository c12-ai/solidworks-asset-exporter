using System;
using System.IO;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class WanxiangRegistrySnapshot
    {
        public AssetRegistryDocument Registry { get; set; }
        public bool RemoteRegistryExists { get; set; }
        public string RemotePath { get; set; }
    }

    public sealed class WanxiangRegistryProvider
    {
        public WanxiangRegistrySnapshot Fetch(ExporterSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            settings.ValidateWanxiang();
            using (var client = new WanxiangDataClient(settings.WanxiangBaseUrl, settings.WanxiangApiKey))
                return Fetch(client, settings);
        }

        public WanxiangRegistrySnapshot Fetch(WanxiangDataClient client, ExporterSettings settings)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (settings == null) throw new ArgumentNullException("settings");
            settings.ValidateWanxiang();

            var remotePath = WanxiangRemoteLayout.AssetRegistryEndpoint;
            var downloaded = Path.Combine(Path.GetTempPath(), "wanxiang-registry-preview-" +
                Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var download = client.DownloadAssetRegistry(downloaded);
                var registry = AssetRegistryStore.LoadFromFile(downloaded);
                if (settings.SaveRegistryLocally)
                    AssetRegistryStore.Replace(settings.AssetLibraryRoot, registry);
                return new WanxiangRegistrySnapshot
                {
                    Registry = registry,
                    RemoteRegistryExists = download.Exists,
                    RemotePath = remotePath
                };
            }
            finally
            {
                if (File.Exists(downloaded)) File.Delete(downloaded);
            }
        }
    }
}
