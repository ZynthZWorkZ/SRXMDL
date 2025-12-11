using System.Text.Json.Serialization;

namespace SRXMDL.Login
{
    public class LoginCred
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("passwordProtected")]
        public string PasswordProtected { get; set; } = string.Empty;
    }
}

