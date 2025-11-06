using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;
using System.Security.Cryptography;
using firstconnectfirebase.Services;

namespace firstconnectfirebase.Controllers
{
    public class AuthController : Controller
    {
        private readonly IConfiguration _config;
        public AuthController(IConfiguration config) => _config = config;

        public class LoginDto { public string Email { get; set; } public string Password { get; set; } }


        [HttpGet]
        public IActionResult Login()
        {
            // If already logged in, go straight to Home
            if (HttpContext.Session.GetString("IsLoggedIn") == "true" &&
                !string.IsNullOrEmpty(HttpContext.Session.GetString("UserUid")) &&
                !string.IsNullOrEmpty(HttpContext.Session.GetString("IdToken")))
            {
                return RedirectToAction("Index", "Home");
            }
            return View();
        }


        private static string TrimTrailingSlash(string s) =>
            string.IsNullOrWhiteSpace(s) ? s : s.TrimEnd('/');


        // Helper to read from either ":" or "__" keys
        private string GetConfig(params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = _config[k];
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private static string ExtractFirebaseError(string responseBody)
        {
            try
            {
                dynamic err = JsonConvert.DeserializeObject(responseBody);
                string msg = err.error.message;
                return msg switch
                {
                    "EMAIL_NOT_FOUND" => "Email not found.",
                    "INVALID_EMAIL" => "Invalid email format.",
                    "INVALID_PASSWORD" => "Incorrect password.",
                    _ => "Invalid email or password. Please try again! "
                };
            }
            catch { return null; }
        }




        [IgnoreAntiforgeryToken]             // JSON posts don't have antiforgery token
        [HttpPost]                            // conventional route => /Auth/LoginAjax
        public async Task<IActionResult> LoginAjax([FromBody] LoginDto model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Password))
                return BadRequest(new { success = false, message = "Email and password are required." });

            // Identity Toolkit uses the Web API Key (fall back to your old name "Secret" if needed)
            var apiKey = GetConfig("Firebase:WebApiKey", "Firebase__WebApiKey", "Firebase:Secret", "Firebase__Secret");
            var firebaseDbUrl = GetConfig("Firebase:DatabaseUrl", "Firebase__DatabaseUrl")?.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(firebaseDbUrl))
                return StatusCode(500, new { success = false, message = "Server auth not configured." });

            using var client = new HttpClient();
            var payload = new { email = model.Email, password = model.Password, returnSecureToken = true };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={apiKey}", content);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                var error = ExtractFirebaseError(body) ?? "Login failed. Incorrect email or password.";
                return Unauthorized(new { success = false, message = error });
            }

            dynamic json = JsonConvert.DeserializeObject(body);
            string idToken = json.idToken;
            string localId = json.localId;

            // Set session
            HttpContext.Session.SetString("IsLoggedIn", "true");
            HttpContext.Session.SetString("UserUid", localId);
            HttpContext.Session.SetString("IdToken", idToken);

            // Store login user ROLE in session
            var userRes = await client.GetAsync($"{firebaseDbUrl}/users/{localId}.json?auth={idToken}");
            var userJson = await userRes.Content.ReadAsStringAsync();
            dynamic userObj = JsonConvert.DeserializeObject(userJson);
            HttpContext.Session.SetString("Role", (string)(userObj?.role ?? ""));

            // Tell client where to go next
            return Ok(new { success = true, message = "Login successful.", redirect = Url.Action("Index", "Home") });
        }



        [HttpGet]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login");
        }










        [HttpGet]
        public IActionResult SignUp()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> SignUp(string email, string password, string name, string phone, string role)
        {
            using var client = new HttpClient();
            var payload = new { email, password, returnSecureToken = true };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var apiKey = Environment.GetEnvironmentVariable("Firebase__Secret");
            var firebaseDbUrl = Environment.GetEnvironmentVariable("Firebase__DatabaseUrl");

            // 1. Register account in firebase Auth
            var authResponse = await client.PostAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={apiKey}",
                content);
            var authResult = await authResponse.Content.ReadAsStringAsync();
            string successMessage = "Register successful.";
            string failMessage = "Register failed.";

            if (authResponse.IsSuccessStatusCode)
            {
                dynamic json = JsonConvert.DeserializeObject(authResult);
                string localId = json.localId;
                string idToken = json.idToken;

                // 2. Write into firebase realtime
                var userData = new { email, name, phone, role, createdAt = DateTime.UtcNow.ToString("o") };
                var dbContent = new StringContent(JsonConvert.SerializeObject(userData), Encoding.UTF8, "application/json");
                await client.PutAsync(
                    $"{firebaseDbUrl}/users/{localId}.json?auth={idToken}",
                    dbContent);
            }

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return HandleAjaxResponse(authResponse, authResult, successMessage, failMessage);
            }

            return HandleNormalResponse(authResponse, authResult, successMessage, failMessage);
        }








        [HttpGet]
        public IActionResult ForgotPassword() => View();

        [IgnoreAntiforgeryToken]
        [HttpPost]
        public async Task<IActionResult> ForgotPassword([FromBody] Dictionary<string, string> body)
        {
            var email = body != null && body.TryGetValue("email", out var e) ? e : null;
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest(new { success = false, message = "Email is required." });

            var apiKey = GetConfig("Firebase:WebApiKey", "Firebase__WebApiKey", "Firebase:Secret", "Firebase__Secret");

            using var client = new HttpClient();
            var payload = new { requestType = "PASSWORD_RESET", email };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var resp = await client.PostAsync($"https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={apiKey}", content);
            var txt = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                var err = ExtractFirebaseError(txt) ?? "Failed to send reset link.";
                return BadRequest(new { success = false, message = err });
            }

            return Ok(new { success = true, message = "If this email is registered, a password reset link has been sent." });
        }






        private IActionResult HandleAjaxResponse(HttpResponseMessage response, string result, string successMessage, string failMessage)
        {
            if (response.IsSuccessStatusCode)
            {
                return Json(new { success = true, message = successMessage });
            }

            // Parse Firebase error
            var error = ExtractFirebaseError(result);
            return Json(new { success = false, message = error ?? failMessage });
        }


        private IActionResult HandleNormalResponse(HttpResponseMessage response, string result, string successMessage, string failMessage)
        {
            if (response.IsSuccessStatusCode)
            {
                TempData["Success"] = successMessage;
                return RedirectToAction("Index", "Home");
            }

            var error = ExtractFirebaseError(result);
            ViewBag.Error = error ?? failMessage;
            return View();
        }

        


    }
}