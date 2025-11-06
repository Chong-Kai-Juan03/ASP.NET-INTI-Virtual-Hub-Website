using Firebase.Database;
using Firebase.Database.Query;
using System;
using firstconnectfirebase.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static System.Formats.Asn1.AsnWriter;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace firstconnectfirebase.Services
{
    public class FirebaseService
    {

        private readonly IHttpClientFactory _http;
        private readonly IHttpContextAccessor _ctx;
        private readonly string _dbUrl; // no trailing slash

        public FirebaseService(IHttpClientFactory http, IHttpContextAccessor ctx, IConfiguration cfg)
        {
            _http = http;
            _ctx = ctx;
            _dbUrl = (cfg["Firebase:DatabaseUrl"] ?? cfg["Firebase__DatabaseUrl"])?.TrimEnd('/')
                     ?? throw new InvalidOperationException("Firebase DatabaseUrl not configured.");
        }

        private string? IdToken => _ctx.HttpContext?.Session?.GetString("IdToken");
        private HttpClient Client() => _http.CreateClient();




        // ------------------------------------------------------------
        // Helper: Resolve the correct path for a scene node based on
        // your schema (nested vs flat).
        //
        // Nested: Scenes/{Building}/{Level}/{SceneId}
        //   Flat: Scenes/{Building}/{SceneId}
        //
        // Rule used here:
        // - If level is null/empty/"N/A" (case-insensitive), we write flat.
        // - Otherwise we write nested.
        // ------------------------------------------------------------
        private static string ResolveScenePath(string building, string level, string sceneId)
        {
            var b = (building ?? "").Trim();
            var l = (level ?? "").Trim();
            var id = (sceneId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(b) || string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Building and SceneId are required.");

            bool isFlat = string.IsNullOrWhiteSpace(l) || l.Equals("NA", StringComparison.OrdinalIgnoreCase);
            return isFlat ? $"Scenes/{b}/{id}" : $"Scenes/{b}/{l}/{id}";
        }





        // ------------------------------------------------------------
        // Get all scenes - main function for reading all scenes from Firebase.
        // ------------------------------------------------------------
        // Function to get all Scene info from Firebase (Image link, SceneTitle, Last update, Person In‑charge)
        public async Task<List<Scene>> GetAllScenes()
        {
            var all = new List<Scene>();

            if (string.IsNullOrEmpty(IdToken))
                throw new InvalidOperationException("Missing IdToken in session.");

            var url = $"{_dbUrl}/Scenes.json?auth={IdToken}";

            //Creates a new HTTP client and sends a GET request to Firebase to retrieve all scene data in JSON format
            using var client = Client(); 
            var res = await client.GetAsync(url); 

            // Validate the status
            if (!res.IsSuccessStatusCode)
                throw new Exception($"Firebase GET failed: {(int)res.StatusCode} {res.ReasonPhrase}");

            var json = await res.Content.ReadAsStringAsync(); //Reads the entire Firebase response and stores it as a JSON string.
            if (string.IsNullOrWhiteSpace(json) || json.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
                return all;

            // defensive parse
            JToken token;
            try { token = JToken.Parse(json); }
            catch { return all; }

            if (token.Type != JTokenType.Object) return all;
            var data = (JObject)token;
            foreach (var b in data.Properties())
            {
                var buildingName = b.Name;
                if (b.Value is not JObject levelGroup) continue;

                foreach (var levelProp in levelGroup.Properties())
                {
                    var levelName = levelProp.Name;
                    if (levelProp.Value.Type != JTokenType.Object) continue;

                    var node = (JObject)levelProp.Value;

                    foreach (var sceneProp in node.Properties())
                    {
                        if (sceneProp.Value is not JObject so) continue;
                        try
                        {
                            var s = so.ToObject<Scene>();
                            if (s != null)
                            {
                                s.SceneId = sceneProp.Name;
                                s.Building = buildingName;
                                s.Level = levelName;
                                all.Add(s);
                            }
                        }
                        catch { /* skip invalid node */ }
                    }
                }
            }

            return all;
        }

        // CAllED WHEN:  Admin edits a scene title or assigns a person, it updates Firebase instantly
        public async Task<bool> UpdateTitleAndPersonAsync(
           string sceneId,
           string building,
           string level,
           string sceneTitle,
           string personInCharge)
        {
            var path = ResolveScenePath(building, level, sceneId);
            var patch = new
            {
                SceneTitle = sceneTitle ?? "",
                PersonInCharge = personInCharge ?? "",
                LastUpdate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            return await PatchAsync(path, patch);
        }


        // CAllED WHEN: replacing or re-uploading an image in AWS S3
        /// Update ImageUrl, S3Key, SceneTitle, PersonInCharge, LastUpdate for a scene.
        public async Task<bool> UpdateSceneAfterS3ReplaceAsync(Scene scene)
        {
            if (string.IsNullOrWhiteSpace(scene.SceneId) || string.IsNullOrWhiteSpace(scene.Building))
                throw new ArgumentException("SceneId and Building are required.");

            scene.LastUpdate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var path = ResolveScenePath(scene.Building!, scene.Level ?? "", scene.SceneId!);
            var patch = new
            {
                scene.ImageUrl,
                scene.S3Key,
                scene.SceneTitle,
                scene.PersonInCharge,
                scene.LastUpdate
            };
            return await PatchAsync(path, patch);
        }


        // HELPER: Lightweight PATCH helper
        // Purpose: Prevents accidental data loss — it updates part of a scene data instead of overwriting the entire record.
        private async Task<bool> PatchAsync(string path, object patch)
        {
            if (string.IsNullOrEmpty(IdToken))
                throw new InvalidOperationException("Missing IdToken in session.");

            var url = $"{_dbUrl}/{path}.json?auth={IdToken}";
            var body = new StringContent(JsonConvert.SerializeObject(patch), Encoding.UTF8, "application/json");

            using var client = Client();
            var req = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = body };
            var res = await client.SendAsync(req);
            return res.IsSuccessStatusCode;
        }


    }


}
