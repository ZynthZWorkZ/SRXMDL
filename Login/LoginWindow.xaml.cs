using System.Windows;

namespace SRXMDL.Login
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var cred = CredentialStore.Load();
            if (cred != null)
            {
                EmailBox.Text = cred.Value.Email;
                StatusText.Text = "Loaded saved email. Password remains encrypted.";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var email = EmailBox.Text?.Trim() ?? string.Empty;
            var password = PasswordBox.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                StatusText.Text = "Email and password are required.";
                return;
            }

            var ok = CredentialStore.Save(email, password);
            StatusText.Text = ok
                ? "Saved. Password is encrypted with your Windows account."
                : "Could not save credentials.";
            if (ok)
            {
                PasswordBox.Password = string.Empty;
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            CredentialStore.Clear();
            EmailBox.Text = string.Empty;
            PasswordBox.Password = string.Empty;
            StatusText.Text = "Credentials cleared. Enter new email and password to save.";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

