using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CraftBox.Core
{
    public class Account
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Age { get; set; }
        public float ColorR { get; set; }
        public float ColorG { get; set; }
        public float ColorB { get; set; }
    }

    public class AccountManager
    {
        readonly string path;
        public List<Account> Accounts { get; private set; } = new List<Account>();
        public Account Active { get; set; }

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public AccountManager(string dataDir)
        {
            path = Path.Combine(dataDir, "accounts.json");
            Load();
            if (Accounts.Count == 0)
                SeedDefaults();
        }

        void Load()
        {
            try
            {
                if (File.Exists(path))
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                    {
                        foreach (var e in doc.RootElement.EnumerateArray())
                        {
                            Accounts.Add(new Account
                            {
                                Id = Str(e, "id"), Name = Str(e, "name"), Age = Int(e, "age"),
                                ColorR = Flt(e, "r"), ColorG = Flt(e, "g"), ColorB = Flt(e, "b"),
                            });
                        }
                    }
                }
            }
            catch { Accounts = new List<Account>(); }
        }

        void SeedDefaults()
        {
            Accounts = new List<Account>
            {
                new Account { Id = "hafiz", Name = "Hafiz", Age = 0, ColorR = 0.898f, ColorG = 0.035f, ColorB = 0.078f },
                new Account { Id = "ain", Name = "Ain", Age = 0, ColorR = 0.608f, ColorG = 0.349f, ColorB = 0.714f },
                new Account { Id = "rauhah", Name = "Rauhah", Age = 14, ColorR = 0.914f, ColorG = 0.118f, ColorB = 0.388f },
                new Account { Id = "aqil", Name = "Aqil", Age = 12, ColorR = 0.129f, ColorG = 0.588f, ColorB = 0.953f },
                new Account { Id = "uwais", Name = "Uwais", Age = 9, ColorR = 0.298f, ColorG = 0.686f, ColorB = 0.314f },
                new Account { Id = "hadif", Name = "Hadif", Age = 6, ColorR = 1f, ColorG = 0.596f, ColorB = 0f },
            };
            Save();
        }

        public void Save()
        {
            try
            {
                var arr = Accounts.Select(a => new Dictionary<string, object>
                {
                    ["id"] = a.Id, ["name"] = a.Name, ["age"] = a.Age,
                    ["r"] = a.ColorR, ["g"] = a.ColorG, ["b"] = a.ColorB,
                }).ToList();
                File.WriteAllText(path, JsonSerializer.Serialize(arr, Json));
            }
            catch { }
        }

        public string HistoryKey => $"play_history_{Active?.Id ?? "default"}";
        public string PinnedKey => $"pinned_titles_{Active?.Id ?? "default"}";
        public string LikesKey => $"likes_{Active?.Id ?? "default"}";
        public string DislikesKey => $"dislikes_{Active?.Id ?? "default"}";

        static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        static float Flt(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0;
    }
}
