using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using firstconnectfirebase.Services;
using firstconnectfirebase.Models;

namespace firstconnectfirebase.Controllers
{
    public class SceneController : Controller
    {
        private readonly ILogger<SceneController> _logger;
        private readonly IConfiguration _config;

        // Use the interface; the service reads IdToken from session
        private readonly FirebaseService _firebaseService;

        // S3 is optional; DI will pass null if not registered
        private readonly S3Service? _s3Service;

        private readonly string _firebaseDbUrl;
        private readonly string _apiKey;

        public SceneController(
            ILogger<SceneController> logger,
            IConfiguration config,
            FirebaseService firebaseService,
            S3Service? s3Service = null)
        {
            _logger = logger;
            _config = config;
            _firebaseService = firebaseService;
            _s3Service = s3Service;

            // Read from Azure App Settings (supports ":" and "__")
            _firebaseDbUrl =
                (_config["Firebase:DatabaseUrl"] ?? _config["Firebase__DatabaseUrl"])?.TrimEnd('/') ?? "";

            // Identity Toolkit REST key (fallback to old name "Secret" if you kept it in .env)
            _apiKey =
                _config["Firebase:Secret"] ?? _config["Firebase__Secret"] ?? "";
        }



        [HttpGet]
        public IActionResult Manage360()
        {
            var isLoggedIn = HttpContext.Session.GetString("IsLoggedIn") == "true";
            var idToken = HttpContext.Session.GetString("IdToken");

            if (!isLoggedIn || string.IsNullOrEmpty(idToken))
                return RedirectToAction("Login", "Auth");

            return View(); // no server-side fetch here
        }






        [HttpGet]
        public IActionResult Debug()
        {
            return Json(new
            {
                loggedIn = HttpContext.Session.GetString("IsLoggedIn"),
                hasIdToken = !string.IsNullOrEmpty(HttpContext.Session.GetString("IdToken")),
                hasDbUrl = !string.IsNullOrWhiteSpace(_firebaseDbUrl),
                hasApiKey = !string.IsNullOrWhiteSpace(_apiKey),
            });
        }






        // Count scenes using direct HTTP (kept close to your code)
        [HttpGet]
        public async Task<IActionResult> GetSceneCounts()
        {
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(idToken)) return Unauthorized();

            if (string.IsNullOrWhiteSpace(_firebaseDbUrl))
                return StatusCode(500, "Database URL not configured.");

            var url = $"{_firebaseDbUrl}/Scenes.json?auth={idToken}";
            using var client = new HttpClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, "Failed to load scene data.");

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json) || json == "null")
                return Json(new { total = 0, inti = 0, levels = new { } });

            var data = JObject.Parse(json);

            int total = 0, inti = 0;
            var levelCounts = new Dictionary<string, int>();

            // Count scenes under INTI-Penang 
            if (data["Inti-Penang"] is JObject intiObj)
            {
                foreach (var level in intiObj.Properties())
                {
                    var levelName = level.Name; // e.g. "Level 2"
                    var levelScenes = level.Value as JObject;
                    int levelCount = levelScenes?.Count ?? 0;
                    levelCounts[levelName] = levelCount;
                    inti += levelCount;
                }
            }
            total = inti;
            //  Return structured result
            return Json(new
            {
                total,
                inti,
                levels = levelCounts
            });
        }






        [HttpGet]
        public async Task<IActionResult> GetAllScenes()
        {
            try
            {
                var idToken = HttpContext.Session.GetString("IdToken");
                if (string.IsNullOrEmpty(idToken)) return Unauthorized();

                var scenes = await _firebaseService.GetAllScenes();
                return Json(scenes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch scenes");
                return StatusCode(500, new { error = "Failed to fetch scenes", details = ex.Message });
            }
        }





        /* ----------------------- Helpers for file keys ----------------------- */

        private static string Norm(string s) => (s ?? "Unassigned").Trim().Trim('/');

        private static string BuildS3Key(string building, string level, string originalFileName)
        {
            var b = Norm(building);
            var l = Norm(level);
            var ext = Path.GetExtension(originalFileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            var fname = $"{Guid.NewGuid()}{ext}";
            return $"{b}/{l}/{fname}";
        }

        /* ----------------------- S3 endpoints (optional) --------------------- */

        [IgnoreAntiforgeryToken]
        [HttpPost]
        public async Task<IActionResult> UpdateSceneWithImage(
            [FromForm] IFormFile? imageFile,
            [FromForm] string? sceneId,
            [FromForm] string? building,
            [FromForm] string? level,
            [FromForm] string? sceneTitle,
            [FromForm] string? personInCharge)
        {
            if (_s3Service is null)
                return StatusCode(501, new { message = "S3 is not configured on the server." });

            if (imageFile == null || imageFile.Length == 0)
                return BadRequest(new { message = "No image provided." });

            try
            {
                var key = BuildS3Key(building ?? "Inti-Penang", level ?? "Unassigned", imageFile.FileName);
                _logger.LogInformation("[S3 Upload] Using key: {Key}", key);

                var imageUrl = await _s3Service.UploadFileAsync(imageFile, key);

                // (Optional) update Firebase fields if needed using _firebaseService + session IdToken

                return Json(new { success = true, imageUrl, s3Key = key });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "S3 upload failed");
                return StatusCode(500, new { message = "S3 upload failed." });
            }
        }

        [IgnoreAntiforgeryToken]
        [HttpPost]
        [RequestSizeLimit(100_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
        public async Task<IActionResult> ReplaceSceneImage(
            [FromForm] IFormFile? imageFile,
            [FromForm] string sceneId,
            [FromForm] string building,
            [FromForm] string level,
            [FromForm] string sceneTitle,
            [FromForm] string personInCharge,
            [FromForm] string? oldS3Key,
            [FromForm] string? oldImageUrl)
        {
            if (_s3Service is null)
                return StatusCode(501, new { message = "S3 is not configured on the server." });

            try
            {
                // Case A: metadata-only
                if (imageFile == null || imageFile.Length == 0)
                {
                    var ok = await _firebaseService.UpdateTitleAndPersonAsync(
                        sceneId: sceneId,
                        building: building,
                        level: level,
                        sceneTitle: sceneTitle,
                        personInCharge: personInCharge
                    );

                    if (!ok) return StatusCode(500, new { message = "Failed to update metadata in Firebase." });
                    return Json(new { success = true, imageUrl = (string?)null, s3Key = (string?)null });
                }

                // Case B: replace image + update metadata
                var newKey = BuildS3Key(building ?? "Inti-Penang", level ?? "Unassigned", imageFile.FileName);
                var newUrl = await _s3Service.UploadFileAsync(imageFile, newKey);

                // Delete old S3 object (best-effort)
                if (!string.IsNullOrWhiteSpace(oldS3Key))
                {
                    try { await _s3Service.DeleteFileAsync(oldS3Key); }
                    catch (System.Exception delEx)
                    {
                        _logger.LogWarning(delEx, "Failed to delete old S3 object: {Key}", oldS3Key);
                    }
                }

                var sceneUpdate = new Scene
                {
                    SceneId = sceneId,
                    Building = building,
                    Level = level,
                    SceneTitle = sceneTitle,
                    PersonInCharge = personInCharge,
                    ImageUrl = newUrl,
                    S3Key = newKey
                };

                var fbOk = await _firebaseService.UpdateSceneAfterS3ReplaceAsync(sceneUpdate);
                if (!fbOk)
                {
                    _logger.LogWarning("Firebase update failed for scene {SceneId} at {Building}/{Level}", sceneId, building, level);
                }

                return Json(new { success = true, imageUrl = newUrl, s3Key = newKey });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "ReplaceSceneImage failed for {SceneId}", sceneId);
                return StatusCode(500, new { message = "Failed to replace scene image." });
            }
        }


        [HttpGet]
        public IActionResult GetDownloadUrl([FromQuery] string key)
        {
            if (_s3Service is null)
                return StatusCode(501, new { message = "S3 is not configured on the server." });

            if (string.IsNullOrWhiteSpace(key))
                return BadRequest(new { message = "Missing key." });

            try
            {
                var fileName = Path.GetFileName(key);
                var url = _s3Service.GetPresignedGetUrlWithAttachment(
                    key,
                    fileName,
                    TimeSpan.FromMinutes(10));
                return Json(new { url });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "GetDownloadUrl failed for key {Key}", key);
                return StatusCode(500, new { message = "Failed to generate download URL." });
            }
        }
    }
}
