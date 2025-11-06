using Microsoft.AspNetCore.Mvc;
using firstconnectfirebase.Services;
using firstconnectfirebase.Models;
using System.Diagnostics;
using CloudinaryDotNet;
using System.Security.Cryptography;
using System.Text;
using FirebaseAdmin.Auth;
using System;
using HttpMethod = System.Net.Http.HttpMethod;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Principal;
using System.Net;
using System.Text.Json;

namespace firstconnectfirebase.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly FirebaseService _firebaseService;
        private readonly string _firebaseDbUrl;   // no throws in ctor
        private readonly string _firebaseApiKey;

        public HomeController(ILogger<HomeController> logger, IConfiguration config)
        {
            _logger = logger;
            // read from either "Firebase:DatabaseUrl" or "Firebase__DatabaseUrl"
            _firebaseDbUrl =
                (config["Firebase:DatabaseUrl"] ?? config["Firebase__DatabaseUrl"])?.TrimEnd('/');

            // IMPORTANT: Use Web API Key. Fallback to your old Secret key name to be backward compatible.
            _firebaseApiKey =
                config["Firebase:Secret"] ?? config["Firebase__Secret"];
        }



        public async Task<IActionResult> Index()
        {
            var userUid = HttpContext.Session.GetString("UserUid");
            var idToken = HttpContext.Session.GetString("IdToken");

            // Not logged in → go to login
            if (string.IsNullOrEmpty(userUid) || string.IsNullOrEmpty(idToken))
                return RedirectToAction("Login", "Auth");


            // DB URL missing → also go to login (or show a friendly page)
            if (string.IsNullOrWhiteSpace(_firebaseDbUrl))
                return RedirectToAction("Login", "Auth");

            try
            {
                using var http = new HttpClient();
                var resp = await http.GetAsync($"{_firebaseDbUrl}/users/{userUid}.json?auth={idToken}");

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    HttpContext.Session.Clear();               // token expired/invalid
                    return RedirectToAction("Login", "Auth");
                }

                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync();
                dynamic user = string.IsNullOrWhiteSpace(json) ? null : JsonConvert.DeserializeObject(json);

                ViewBag.Name = user?.name ?? "";
                ViewBag.Email = user?.email ?? "";
                ViewBag.Phone = user?.phone ?? "";
                ViewBag.Role = user?.role ?? "";

                return View(); // Views/Home/Index.cshtml (case-sensitive on Linux)
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to load Home/Index.");
                return RedirectToAction("Login", "Auth");
            }
        }


        [HttpGet]
        public async Task<IActionResult> GetPerformanceData()
        {
            try
            {
                var userUid = HttpContext.Session.GetString("UserUid");
                var idToken = HttpContext.Session.GetString("IdToken");

                if (string.IsNullOrEmpty(userUid) || string.IsNullOrEmpty(idToken))
                    return Unauthorized("Session expired or not logged in.");

                using var client = new HttpClient();
                var url = $"{_firebaseDbUrl}/counters/daily.json?auth={idToken}";

                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, "Firebase access failed.");

                var json = await response.Content.ReadAsStringAsync();
                var dailyDict = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(json);

                var monthCount = new Dictionary<string, int>();

                if (dailyDict != null && dailyDict.Count > 0)
                {
                    foreach (var dayEntry in dailyDict)
                    {
                        string dateKey = dayEntry.Key;

                        if (DateTime.TryParseExact(dateKey, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                        {
                            string month = dt.ToString("yyyy-MM");

                            if (!monthCount.ContainsKey(month))
                                monthCount[month] = 0;

                            // Each scene has { title, views }
                            var scenes = dayEntry.Value as Newtonsoft.Json.Linq.JObject;
                            if (scenes != null)
                            {
                                foreach (var scene in scenes.Properties())
                                {
                                    var views = scene.Value["views"]?.ToObject<int>() ?? 0;
                                    monthCount[month] += views;
                                }
                            }
                        }
                    }
                }

                // Ensure last 6 months always exist
                for (int i = 5; i >= 0; i--)
                {
                    var d = DateTime.UtcNow.AddMonths(-i);
                    var month = d.ToString("yyyy-MM");
                    if (!monthCount.ContainsKey(month))
                        monthCount[month] = 0;
                }

                var result = monthCount
                    .OrderBy(x => x.Key)
                    .Select(x => new { month = x.Key, count = x.Value })
                    .ToList();

                return Json(result);

            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to get performance data: {ex.Message}");
            }
        }







        [HttpGet]
        public IActionResult ManageAccount()
        {
            var role = HttpContext.Session.GetString("Role");

            // Only allow Admin to access manage account page
            if (HttpContext.Session.GetString("Role") != "Admin")
                return RedirectToAction("Index", "Home");
            return View();
        }


        [HttpGet]
        public async Task<IActionResult> GetAllUsers()
        {
            // Step 1: Check if session token is present (user must be logged in)
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(idToken))
                return Unauthorized("Missing token.");

            try
            {
                // Step 2: Prepare Firebase Realtime Database URL to fetch all users
                var url = $"{_firebaseDbUrl}/users.json?auth={idToken}";

                // Step 3: Send GET request to Firebase
                using var client = new HttpClient();
                var response = await client.GetAsync(url);

                // Step 4: Handle potential failure
                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, "Failed to fetch users.");

                // Step 5: Read and return raw JSON response to front-end
                var json = await response.Content.ReadAsStringAsync();
                return Content(json, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }


        [HttpPost]
        public async Task<IActionResult> CreateAccount(string email, string password, string name, string phone, string role)
        {
            //  Ensure only Admins can create new accounts
            if (HttpContext.Session.GetString("Role") != "Admin")
                return Unauthorized("Only admins can create accounts.");
            try
            {        
                var payload = new
                {
                    email,
                    password,
                    returnSecureToken = true
                };
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                using var client = new HttpClient();

                var authResponse = await client.PostAsync(
                    $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={_firebaseApiKey}",
                    content);

                var authResult = await authResponse.Content.ReadAsStringAsync();

                if (!authResponse.IsSuccessStatusCode)
                {
                    // Return error from Firebase Auth if registration fails
                    return Content(authResult, "application/json");
                }

                // Parse Firebase Auth response to extract UID and token
                dynamic json = JsonConvert.DeserializeObject(authResult);
                string localId = json.localId;
                string idToken = json.idToken;

                // Prepare user profile data for Firebase Realtime Database
                var userData = new
                {
                    email,
                    name,
                    phone,
                    role,
                    createdAt = DateTime.UtcNow.ToString("o")
                };

                var dbContent = new StringContent(JsonConvert.SerializeObject(userData), Encoding.UTF8, "application/json");

                // Save user info to Realtime Database under /users/{uid}
                var dbUrl = _firebaseDbUrl; // from your class-level variable
                var dbRes = await client.PutAsync($"{dbUrl}/users/{localId}.json?auth={idToken}", dbContent);

                if (!dbRes.IsSuccessStatusCode)
                    return StatusCode((int)dbRes.StatusCode, "Failed to write user data to database.");

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Server er ror: {ex.Message}");
            }
        }


        [HttpPost]
        public async Task<IActionResult> DeleteAccount(string uid)
        {
            // Admin role verification
            if (HttpContext.Session.GetString("Role") != "Admin")
                return Unauthorized("Only admins can delete accounts.");

            var idToken = HttpContext.Session.GetString("IdToken");
            var currentUserUid = HttpContext.Session.GetString("UserUid");

            // Prevent admin from deleting their own account
            if (uid == currentUserUid)
            {
                return Json(new { success = false, message = "You cannot delete your own account." });
            }

            using var client = new HttpClient();

            // Get all users to count the number of Admins
            var userListRes = await client.GetAsync($"{_firebaseDbUrl}/users.json?auth={idToken}");
            var jsonStr = await userListRes.Content.ReadAsStringAsync();
            var userDict = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(jsonStr);

            int adminCount = userDict?.Values.Count(u => (string)u.role == "Admin") ?? 0;

            // Prevent deletion of the last Admin account
            if (userDict.TryGetValue(uid, out var user) &&
                (string)user.role == "Admin" &&
                adminCount <= 1)
            {
                return Json(new { success = false, message = "Cannot delete the last admin account." });
            }

            try
            {
                // Delete user from Firebase Authentication
                await FirebaseAuth.DefaultInstance.DeleteUserAsync(uid);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Auth deletion failed: {ex.Message}" });
            }

            // Delete user data from Firebase Realtime Database
            var dbRes = await client.DeleteAsync($"{_firebaseDbUrl}/users/{uid}.json?auth={idToken}");

            if (!dbRes.IsSuccessStatusCode)
            {
                return Json(new { success = false, message = "Failed to delete user from database." });
            }

            return Json(new { success = true });
        }







        [HttpGet]
        public async Task<IActionResult> Setting()
        {
            var uid = HttpContext.Session.GetString("UserUid");
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(idToken))
                return RedirectToAction("Login", "Auth");

            using var client = new HttpClient();
            var json = await client.GetStringAsync($"{_firebaseDbUrl}/users/{uid}.json?auth={idToken}");

            var model = string.IsNullOrWhiteSpace(json) || json == "null"
                ? new User()
                : JsonConvert.DeserializeObject<User>(json) ?? new User();

            // If RTDB doesn't have email/role, fallback to Auth / Session
            if (string.IsNullOrWhiteSpace(model.Email))
            {
                try
                {
                    var authUser = await FirebaseAuth.DefaultInstance.GetUserAsync(uid);
                    model.Email = authUser.Email ?? "";
                }
                catch { /* ignore, keep empty */ }
            }
            model.Role ??= HttpContext.Session.GetString("Role") ?? "Staff";

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProfile([FromForm] User model)
        {
            var uid = HttpContext.Session.GetString("UserUid");
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(idToken))
            {
                return IsAjax()
                    ? Json(new { success = false, message = "Session expired. Please log in again." })
                    : RedirectToAction("Login", "Auth");
            }

            var newEmail = model.Email?.Trim() ?? "";

            // Basic email validation (avoid depending on a non-existent enum value)
            if (!new EmailAddressAttribute().IsValid(newEmail))
            {
                return IsAjax()
                    ? Json(new { success = false, message = "Invalid email format." })
                    : (IActionResult)View("Setting", model);
            }

            // --- Update Auth email only if changed ---
            try
            {
                var authUser = await FirebaseAuth.DefaultInstance.GetUserAsync(uid);
                if (!string.Equals(authUser.Email ?? "", newEmail, StringComparison.OrdinalIgnoreCase))
                {
                    await FirebaseAuth.DefaultInstance.UpdateUserAsync(new UserRecordArgs
                    {
                        Uid = uid,
                        Email = newEmail
                    });
                }
            }
            catch (FirebaseAuthException ex)
            {
                var msg = ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists
                    ? "This email is already in use."
                    : $"Failed to update authentication email: {ex.Message}";

                return IsAjax()
                    ? Json(new { success = false, message = msg })
                    : (IActionResult)View("Setting", model);
            }
            catch (Exception ex)
            {
                var msg = $"Failed to update authentication email: {ex.Message}";
                return IsAjax()
                    ? Json(new { success = false, message = msg })
                    : (IActionResult)View("Setting", model);
            }

            // --- Update Realtime Database document ---
            var payload = new
            {
                name = model.Name ?? "",
                phone = model.Phone ?? "",
                email = newEmail
            };

            using var client = new HttpClient();
            var req = new HttpRequestMessage(new HttpMethod("PATCH"),
                $"{_firebaseDbUrl}/users/{uid}.json?auth={idToken}")
            {
                Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
            };

            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return IsAjax()
                    ? Json(new { success = false, message = "Saved in Auth, but failed to update database." })
                    : (IActionResult)View("Setting", model);
            }

            HttpContext.Session.SetString("Email", newEmail);

            if (IsAjax()) return Json(new { success = true });

            TempData["Message"] = "Profile updated.";
            return RedirectToAction("Setting");
        }

        private bool IsAjax()
        {
            var xrw = Request.Headers["X-Requested-With"].ToString();
            if (!string.IsNullOrEmpty(xrw) && xrw == "XMLHttpRequest") return true;
            var accept = Request.Headers["Accept"].ToString();
            return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        }






        [HttpGet]
        public async Task<IActionResult> GetRandomPictures(int count = 4)
        {
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(idToken))
                return Unauthorized(new { message = "Not logged in." });

            using var client = new HttpClient();

            try
            {
                // Read the whole Scenes tree
                var url = $"{_firebaseDbUrl}/Scenes.json?auth={idToken}";
                var json = await client.GetStringAsync(url);

                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return Json(new { images = FallbackImages(count) });

                var token = JToken.Parse(json);
                var images = new List<string>();
                CollectImageUrls(token, images);

                // If nothing found, fallback to local /uploads/ samples
                if (images.Count == 0)
                    return Json(new { images = FallbackImages(count) });

                // Randomize and take N unique
                var rng = new Random();
                var selected = images
                    .Distinct()
                    .OrderBy(_ => rng.Next())
                    .Take(Math.Max(1, count))
                    .ToList();

                return Json(new { images = selected });
            }
            catch
            {
                return Json(new { images = FallbackImages(count) });
            }
        }

        // Recursively collect image URLs from any depth
        private static void CollectImageUrls(JToken t, List<string> list)
        {
            if (t is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    var name = prop.Name;
                    // Your data uses "ImageUrl"; handle case-insensitively
                    if (name.Equals("ImageUrl", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("ThumbnailUrl", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("Url", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = prop.Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            // only accept http(s) links to be safe
                            if (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                list.Add(val);
                            }
                        }
                    }

                    // Recurse into children
                    CollectImageUrls(prop.Value, list);
                }
            }
            else if (t is JArray arr)
            {
                foreach (var child in arr) CollectImageUrls(child, list);
            }
        }


        // Fallback to local samples if Scenes has no images
        private List<string> FallbackImages(int count)
        {
            var pool = new[]
            {
                Url.Content("~/uploads/sample1.jpg"),
                Url.Content("~/uploads/sample2.jpg"),
                Url.Content("~/uploads/sample3.jpg"),
                Url.Content("~/uploads/sample4.jpg"),
                Url.Content("~/uploads/sample5.jpg")
            };
            var rng = new Random();
            return pool.OrderBy(_ => rng.Next()).Take(count).ToList();
        }


        [HttpGet]
        public IActionResult Performance()
        {
            // simple session check so only logged-in users see it
            var uid = HttpContext.Session.GetString("UserUid");
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(idToken))
                return RedirectToAction("Login", "Auth");

            return View(); // renders Views/Home/Performance.cshtml
        }




        [HttpGet]
        public async Task<IActionResult> GetTopScenesDistribution()
        {
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(idToken))
                return Json(new { labels = Array.Empty<string>(), counts = Array.Empty<int>() });

            try
            {
                using var client = new HttpClient();
                // 🔸 Target the globalTotals node in Firebase
                var json = await client.GetStringAsync($"{_firebaseDbUrl}/counters/globalTotals.json?auth={idToken}");

                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return Json(new { labels = Array.Empty<string>(), counts = Array.Empty<int>() });

                var root = Newtonsoft.Json.Linq.JToken.Parse(json);
                var scenes = new List<(string Title, int Views)>();

                if (root is Newtonsoft.Json.Linq.JObject obj)
                {
                    foreach (var sceneProp in obj.Properties())
                    {
                        var sceneData = sceneProp.Value;
                        var title = sceneData["title"]?.ToString() ?? "Unknown";
                        var views = 0;

                        if (int.TryParse(sceneData["views"]?.ToString(), out var parsedViews))
                            views = parsedViews;

                        scenes.Add((title, views));
                    }
                }

                // Sort by views descending and take top 5
                var top5 = scenes
                    .OrderByDescending(s => s.Views)
                    .Take(5)
                    .ToList();

                var labels = top5.Select(s => s.Title).ToList();
                var counts = top5.Select(s => s.Views).ToList();

                return Json(new { labels, counts });
            }
            catch (Exception ex)
            {
                // Optional: log ex.Message
                return Json(new { labels = Array.Empty<string>(), counts = Array.Empty<int>() });
            }
        }


        [HttpGet]
        public async Task<IActionResult> GetPerformanceForecast()
        {
            try
            {
                var idToken = HttpContext.Session.GetString("IdToken");
                if (string.IsNullOrEmpty(idToken))
                    return Unauthorized("Session expired or not logged in.");

                using var client = new HttpClient();
                var url = $"{_firebaseDbUrl}/counters/daily.json?auth={idToken}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, "Firebase access failed.");

                var json = await response.Content.ReadAsStringAsync();
                var dailyDict = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(json);

                var monthCount = new Dictionary<string, int>();

                if (dailyDict != null && dailyDict.Count > 0)
                {
                    foreach (var dayEntry in dailyDict)
                    {
                        string dateKey = dayEntry.Key;

                        if (DateTime.TryParseExact(dateKey, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                        {
                            string month = dt.ToString("yyyy-MM");

                            if (!monthCount.ContainsKey(month))
                                monthCount[month] = 0;

                            var scenes = dayEntry.Value as Newtonsoft.Json.Linq.JObject;
                            if (scenes != null)
                            {
                                foreach (var scene in scenes.Properties())
                                {
                                    var views = scene.Value["views"]?.ToObject<int>() ?? 0;
                                    monthCount[month] += views;
                                }
                            }
                        }
                    }
                }

                // Sort by month
                var history = monthCount
                    .OrderBy(x => x.Key)
                    .Select(x => new { month = x.Key, count = x.Value })
                    .ToList();

                // Simple linear regression forecast for next 3 months
                var counts = history.Select(h => (double)h.count).ToArray();
                var n = counts.Length;
                if (n < 2)
                {
                    return Json(new
                    {
                        history = history,
                        forecast = new List<object>()
                    });
                }

                // x = 1..n
                var xVals = Enumerable.Range(1, n).Select(i => (double)i).ToArray();
                var xMean = xVals.Average();
                var yMean = counts.Average();
                var numerator = xVals.Zip(counts, (x, y) => (x - xMean) * (y - yMean)).Sum();
                var denominator = xVals.Sum(x => Math.Pow(x - xMean, 2));
                var slope = numerator / denominator;
                var intercept = yMean - slope * xMean;

                var forecastList = new List<object>();
                for (int i = 1; i <= 3; i++)
                {
                    var futureX = n + i;
                    var pred = intercept + slope * futureX;
                    if (pred < 0) pred = 0;
                    var futureMonth = DateTime.UtcNow.AddMonths(i).ToString("yyyy-MM");
                    forecastList.Add(new { month = futureMonth, predicted = Math.Round(pred) });
                }

                return Json(new
                {
                    history = history,
                    forecast = forecastList
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to get forecast data: {ex.Message}");
            }
        }




        private static int CountScenesWithImageUrl(JToken token)
        {
            int count = 0;

            if (token is JObject obj)
            {
                // If this object has an ImageUrl, count it as a scene node
                if (obj.Properties().Any(p => p.Name.Equals("ImageUrl", StringComparison.OrdinalIgnoreCase)))
                    count++;

                foreach (var p in obj.Properties())
                    count += CountScenesWithImageUrl(p.Value);
            }
            else if (token is JArray arr)
            {
                foreach (var child in arr)
                    count += CountScenesWithImageUrl(child);
            }

            return count;
        }


        [HttpGet]
        public async Task<IActionResult> GetUserRoleCounts()
        {
            var idToken = HttpContext.Session.GetString("IdToken");
            if (string.IsNullOrEmpty(idToken))
                return Json(new { admin = 0, staff = 0 });

            try
            {
                using var client = new HttpClient();
                var json = await client.GetStringAsync($"{_firebaseDbUrl}/users.json?auth={idToken}");
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return Json(new { admin = 0, staff = 0 });

                var users = JObject.Parse(json);
                int admin = 0, staff = 0;

                foreach (var kv in users)
                {
                    // accept either "role" or "Role"
                    var roleToken = kv.Value["role"] ?? kv.Value["Role"];
                    var role = roleToken?.ToString()?.Trim();
                    if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)) admin++;
                    else if (string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase)) staff++;
                }

                return Json(new { admin, staff });
            }
            catch
            {
                return Json(new { admin = 0, staff = 0 });
            }
        }





        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}