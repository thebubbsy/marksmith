namespace MarkSmith.ViewModels;

/// <summary>Where Google sign-in stands, for the colour of the Google Docs page's status bar.</summary>
public enum GoogleSignInPhase
{
    /// <summary>No OAuth client yet: sign-in can't start.</summary>
    NotConfigured,
    SignedOut,
    /// <summary>Waiting for the user to enter the device code in their browser.</summary>
    SigningIn,
    Connected,
    Failed,
}
