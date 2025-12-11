using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SRXMDL.Login
{
    public class CookieInfo
    {
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public DateTime? Expires { get; set; }
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
    }

    public class SiriusXmLoginService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;
        private readonly HttpClientHandler _handler;
        private readonly List<CookieInfo> _cookies = new();
        private readonly string _baseUrl = "https://api.edge-gateway.siriusxm.com";
        private int _logicalClock = 0;
        private int _clockCounter = 0;

        public SiriusXmLoginService()
        {
            _cookieContainer = new CookieContainer();
            _handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                UseCookies = true
            };
            _httpClient = new HttpClient(_handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json; charset=utf-8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://www.siriusxm.com");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.siriusxm.com/");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
        }

        private void UpdateClock()
        {
            _clockCounter++;
            _httpClient.DefaultRequestHeaders.Remove("x-sxm-clock");
            _httpClient.DefaultRequestHeaders.Add("x-sxm-clock", $"[{_logicalClock},{_clockCounter}]");
        }

        public void SetAnonymousToken(string token)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<AuthenticatedSessionResponse?> LoginAsync(string email, string password, string bearer)
        {
            if (string.IsNullOrWhiteSpace(bearer) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                return null;

            SetAnonymousToken(bearer);

            var status = await CheckIdentityStatusAsync(email);
            if (status?.HasPassword != true)
                return null;

            var pwd = await AuthenticateWithPasswordAsync(email, password);
            if (pwd == null)
                return null;

            var session = await CreateAuthenticatedSessionAsync(pwd.Grant);
            if (session == null)
                return null;

            BuildClientCookies(session, pwd, email);
            return session;
        }

        private async Task<IdentityStatusResponse?> CheckIdentityStatusAsync(string email)
        {
            try
            {
                UpdateClock();
                var url = $"{_baseUrl}/identity/v1/identities/status?handle={Uri.EscapeDataString(email)}";
                var resp = await _httpClient.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                CaptureCookies(resp, _baseUrl);
                return await resp.Content.ReadFromJsonAsync<IdentityStatusResponse>();
            }
            catch { return null; }
        }

        private async Task<PasswordAuthResponse?> AuthenticateWithPasswordAsync(string email, string password)
        {
            try
            {
                UpdateClock();
                var url = $"{_baseUrl}/identity/v1/identities/authenticate/password";
                var req = new PasswordAuthRequest { Handle = email, Password = password };
                var resp = await _httpClient.PostAsJsonAsync(url, req);
                resp.EnsureSuccessStatusCode();
                CaptureCookies(resp, _baseUrl);
                return await resp.Content.ReadFromJsonAsync<PasswordAuthResponse>();
            }
            catch { return null; }
        }

        private async Task<AuthenticatedSessionResponse?> CreateAuthenticatedSessionAsync(string grant)
        {
            try
            {
                UpdateClock();
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant);
                var url = $"{_baseUrl}/session/v1/sessions/authenticated";
                var resp = await _httpClient.PostAsync(url, new StringContent("\"\"", Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                CaptureCookies(resp, _baseUrl);
                return await resp.Content.ReadFromJsonAsync<AuthenticatedSessionResponse>();
            }
            catch { return null; }
        }

        private void CaptureCookies(HttpResponseMessage resp, string baseUrl)
        {
            if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
            var uri = new Uri(baseUrl);
            foreach (var sc in values)
            {
                var parsed = ParseSetCookie(sc, uri);
                if (parsed != null) _cookies.Add(parsed);
            }
        }

        private CookieInfo? ParseSetCookie(string header, Uri uri)
        {
            try
            {
                var parts = header.Split(';');
                var nv = parts[0].Split('=', 2);
                if (nv.Length != 2) return null;
                var c = new CookieInfo
                {
                    Name = nv[0].Trim(),
                    Value = nv[1].Trim(),
                    Domain = uri.Host,
                    Path = "/",
                    Secure = false,
                    HttpOnly = false
                };
                foreach (var p in parts.Skip(1))
                {
                    var t = p.Trim();
                    var tl = t.ToLowerInvariant();
                    if (tl.StartsWith("expires=") && DateTime.TryParse(t.Substring(8), out var exp))
                        c.Expires = exp;
                    else if (tl.StartsWith("path="))
                        c.Path = t.Substring(5);
                    else if (tl.StartsWith("domain="))
                        c.Domain = t.Substring(7);
                    else if (tl == "secure")
                        c.Secure = true;
                    else if (tl == "httponly")
                        c.HttpOnly = true;
                }
                return c;
            }
            catch { return null; }
        }

        private void BuildClientCookies(AuthenticatedSessionResponse session, PasswordAuthResponse pwd, string email)
        {
            var authTokenData = new
            {
                session = new
                {
                    accessToken = session.AccessToken,
                    accessTokenId = session.AccessTokenId,
                    sessionType = session.SessionType,
                    accessTokenExpiresAt = session.AccessTokenExpiresAt,
                    refreshTokenExpiresAt = session.RefreshTokenExpiresAt,
                    experiments = session.Experiments ?? new List<Experiment>(),
                    location = session.Location ?? new Location { CountryCode = "US", Tz = "America/Chicago", Region = "Kansas", City = null },
                    entitlementHash = session.EntitlementHash ?? ""
                },
                identityGrant = new { identityId = pwd.IdentityId, grant = pwd.Grant },
                handle = email
            };
            var authToken = Uri.EscapeDataString(JsonSerializer.Serialize(authTokenData));
            _cookies.Add(new CookieInfo
            {
                Domain = ".siriusxm.com",
                Path = "/",
                Name = "AUTH_TOKEN",
                Value = authToken,
                Secure = false,
                HttpOnly = false,
                Expires = DateTime.TryParse(session.AccessTokenExpiresAt, out var exp) ? exp : null
            });

            var logicalClock = Uri.EscapeDataString($"[{_logicalClock},{_clockCounter}]");
            _cookies.Add(new CookieInfo { Domain = "www.siriusxm.com", Path = "/", Name = "LOGICAL_CLOCK", Value = logicalClock, Expires = DateTime.UtcNow.AddDays(1) });
            _cookies.Add(new CookieInfo { Domain = ".siriusxm.com", Path = "/", Name = "LOGICAL_CLOCK", Value = logicalClock, Expires = DateTime.UtcNow.AddDays(1) });
            _cookies.Add(new CookieInfo { Domain = "www.siriusxm.com", Path = "/", Name = "s_invisit", Value = "true", Expires = DateTime.UtcNow.AddDays(1) });
        }

        public string ExportCookiesNetscapeFormat()
        {
            CaptureFromContainer();
            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine();
            var unique = _cookies
                .GroupBy(c => new { c.Domain, c.Path, c.Name })
                .Select(g => g.First())
                .OrderBy(c => c.Domain).ThenBy(c => c.Path).ThenBy(c => c.Name);

            foreach (var c in unique)
            {
                var domainFlag = c.Domain.StartsWith(".") ? "TRUE" : "FALSE";
                var secureFlag = c.Secure ? "TRUE" : "FALSE";
                var exp = 0L;
                if (c.Expires.HasValue)
                {
                    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    exp = (long)(c.Expires.Value.ToUniversalTime() - epoch).TotalSeconds;
                }
                sb.AppendLine($"{c.Domain}\t{domainFlag}\t{c.Path}\t{secureFlag}\t{exp}\t{c.Name}\t{c.Value}");
            }
            return sb.ToString();
        }

        private void CaptureFromContainer()
        {
            var domains = new[] { "api.edge-gateway.siriusxm.com", "www.siriusxm.com", ".siriusxm.com" };
            foreach (var d in domains)
            {
                try
                {
                    var uri = new Uri($"https://{d.TrimStart('.')}");
                    var cookies = _cookieContainer.GetCookies(uri);
                    foreach (System.Net.Cookie cookie in cookies)
                    {
                        if (_cookies.Any(c => c.Name == cookie.Name && c.Domain == cookie.Domain && c.Path == cookie.Path))
                            continue;
                        var info = new CookieInfo
                        {
                            Domain = cookie.Domain.StartsWith(".") ? cookie.Domain : "." + cookie.Domain,
                            Path = cookie.Path,
                            Name = cookie.Name,
                            Value = cookie.Value,
                            Expires = cookie.Expires != DateTime.MinValue ? cookie.Expires : null,
                            Secure = cookie.Secure,
                            HttpOnly = cookie.HttpOnly
                        };
                        _cookies.Add(info);
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _handler?.Dispose();
        }
    }

    // Models
    public class IdentityStatusResponse
    {
        public string IdentityId { get; set; } = string.Empty;
        public bool HasPassword { get; set; }
        public bool HasPasskey { get; set; }
        public List<ContactOption>? ContactOptions { get; set; }
        public bool HasUsedContentSampling { get; set; }
    }

    public class ContactOption
    {
        public string Type { get; set; } = string.Empty;
        public string MaskedValue { get; set; } = string.Empty;
    }

    public class PasswordAuthRequest
    {
        public string Handle { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class PasswordAuthResponse
    {
        public string IdentityId { get; set; } = string.Empty;
        public string Grant { get; set; } = string.Empty;
    }

    public class AuthenticatedSessionResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string AccessTokenId { get; set; } = string.Empty;
        public string SessionType { get; set; } = string.Empty;
        public string AccessTokenExpiresAt { get; set; } = string.Empty;
        public string RefreshTokenExpiresAt { get; set; } = string.Empty;
        public List<Experiment>? Experiments { get; set; }
        public Location? Location { get; set; }
        public string? EntitlementHash { get; set; }
    }

    public class Experiment
    {
        public int ExperimentId { get; set; }
        public int TreatmentArmId { get; set; }
    }

    public class Location
    {
        public string? CountryCode { get; set; }
        public string? Tz { get; set; }
        public string? Region { get; set; }
        public string? City { get; set; }
    }
}

