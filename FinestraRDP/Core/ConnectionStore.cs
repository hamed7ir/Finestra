using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Finestra.Core
{
    /// <summary>
    /// Persists the list of <see cref="ConnectionProfile"/>s to Documents\Finestra\connections.json
    /// (via <see cref="StoragePaths"/> — Documents-first, RT-safe). File-only; passwords inside are already
    /// DPAPI-protected by the model. Save is atomic (temp + replace) so a crash mid-write can't corrupt the
    /// list. All I/O is guarded — a read failure yields an empty store rather than throwing.
    /// </summary>
    public sealed class ConnectionStore
    {
        public List<ConnectionProfile> Items { get; private set; } = new List<ConnectionProfile>();

        private static ConnectionStore _instance;
        public static ConnectionStore Instance => _instance ?? (_instance = Load());

        private static ConnectionStore Load()
        {
            var store = new ConnectionStore();
            try
            {
                string path = StoragePaths.ConnectionsFile;
                if (File.Exists(path))
                {
                    var items = JsonConvert.DeserializeObject<List<ConnectionProfile>>(File.ReadAllText(path));
                    if (items != null)
                    {
                        // migrate pre-picker connections (custom-W/H only) to the ResolutionMode model, and the old
                        // SmartSizing/DynamicResolution bools to OversizeMode — both idempotent, no data loss
                        foreach (var cp in items)
                            if (cp?.Settings != null) { cp.Settings.MigrateResolution(); cp.Settings.MigrateOversize(); }
                        store.Items = items;
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[CONN] load failed: " + ex.Message); }
            return store;
        }

        public void Save()
        {
            try
            {
                string path = StoragePaths.ConnectionsFile;
                string json = JsonConvert.SerializeObject(Items, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);   // atomic swap
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Debug.WriteLine("[CONN] save failed: " + ex.Message); }
        }

        public ConnectionProfile Find(string id) => Items.FirstOrDefault(c => c.Id == id);

        public void AddOrUpdate(ConnectionProfile cp)
        {
            if (cp == null) return;
            int i = Items.FindIndex(c => c.Id == cp.Id);
            if (i >= 0) Items[i] = cp; else Items.Add(cp);
            Save();
        }

        public void Remove(string id)
        {
            Items.RemoveAll(c => c.Id == id);
            Save();
        }
    }
}
