namespace MarkSmith.Services;

/// <summary>
/// The shipped app's built-in Google OAuth client (Desktop app type), so end users never touch the
/// Google Cloud Console — they just click "Sign in with Google". Desktop-app OAuth secrets are
/// inherently extractable from the binary (that is Google's documented model for installed apps),
/// so baking the client in is standard practice. Power users can still override both values in
/// Settings → Google → Advanced, which takes precedence when filled in.
/// </summary>
public static class GoogleDefaults
{
    // Filled in once by the Marksmith owner after registering a "Desktop app" OAuth client in their
    // Google Cloud project (enable the Google Docs API + Google Drive API; External test mode).
    public const string ClientId = "";
    public const string ClientSecret = "";
}
