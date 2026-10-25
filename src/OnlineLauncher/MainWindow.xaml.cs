using Microsoft.Win32;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace OnlineLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherSettings _settings;
    private CommunityApiClient _api;
    private AuthSession? _session;
    private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private TextBox? _authEmail;
    private PasswordBox? _authPassword;
    private TextBlock? _authMessage;
    private bool _registrationMode;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public MainWindow()
    {
        InitializeComponent();
        _settings = LauncherSettings.Load();
        var initialUrl = IsUsableUrl(_settings.ActiveApiUrl) ? _settings.ActiveApiUrl : "http://127.0.0.1:3000/";
        _api = new CommunityApiClient(initialUrl);
        _session = WindowsSecretStore.Load(_settings.Environment);
        _presenceTimer.Tick += async (_, _) => await SendPresenceHeartbeatAsync();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { _presenceTimer.Stop(); _api.Dispose(); };
        NavigateHome();
        UpdateEnvironmentHeader();
        UpdateAccountHeader();
    }

    private async Task InitializeAsync()
    {
        await RefreshServiceStatusAsync();
        if (_session is null) return;

        try
        {
            if (await EnsureFreshSessionAsync())
            {
                using var account = await AuthorizedGetAsync("v1/client/account");
                UpdateAccountHeader(account.RootElement);
                StartPresenceTimer();
            }
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingError(ex));
        }
    }

    private static bool IsUsableUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task RefreshServiceStatusAsync()
    {
        var health = await _api.CheckServiceAsync();
        StatusDot.Fill = BrushOf(health.IsReady ? "#58B783" : "#D6A63D");
        ApiStatusText.Text = health.IsReady ? "Community API ready" : health.Message;
    }

    private void UpdateEnvironmentHeader()
    {
        EnvironmentText.Text = _settings.Environment == "Public" ? "PUBLIC SERVER" : "LOCAL DEVELOPMENT";
    }

    private void UpdateAccountHeader(JsonElement? accountResponse = null)
    {
        if (_session is null)
        {
            AuthButton.Content = "SIGN IN";
            AccountText.Text = "Signed out · Sign in to use community features";
            return;
        }

        AuthButton.Content = "SIGN OUT";
        var label = _session.Email;
        if (accountResponse is JsonElement root && root.TryGetProperty("player", out var player))
        {
            var displayName = GetString(player, "displayName");
            if (!string.IsNullOrWhiteSpace(displayName)) label = displayName;
        }
        AccountText.Text = $"Signed in · {label}";
    }

    private void SetStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ApiStatusText.Text = message.Length > 65 ? message[..62] + "..." : message;
        StatusDot.Fill = BrushOf("#D6A63D");
    }

    private static string UserFacingError(Exception exception) => exception switch
    {
        ApiException api => api.Message,
        HttpRequestException => "Can't reach the community API. Check the selected environment and whether the backend is running.",
        TaskCanceledException => "The request timed out. Try again.",
        UnauthorizedAccessException => "Windows denied access to a launcher settings or session file.",
        _ => "Something went wrong. Check the API status and try again."
    };

    private async Task<bool> EnsureFreshSessionAsync(bool force = false)
    {
        if (_session is null) return false;
        if (!force && _session.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return true;

        try
        {
            using var response = await _api.PostJsonAsync("v1/auth/refresh", new { refreshToken = _session.RefreshToken });
            var auth = Deserialize<AuthPayload>(response.RootElement);
            if (auth is null || string.IsNullOrWhiteSpace(auth.AccessToken) || string.IsNullOrWhiteSpace(auth.RefreshToken))
                throw new InvalidOperationException("The community API returned an incomplete refresh response.");

            _session.AccessToken = auth.AccessToken;
            _session.RefreshToken = auth.RefreshToken;
            _session.AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, auth.ExpiresIn));
            if (auth.User?.Email is not null) _session.Email = auth.User.Email;
            if (auth.User?.Id is not null) _session.UserId = auth.User.Id;
            WindowsSecretStore.Save(_settings.Environment, _session);
            UpdateAccountHeader();
            StartPresenceTimer();
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearLocalSession();
            return false;
        }
    }

    private async Task<JsonDocument> AuthorizedGetAsync(string path)
    {
        if (_session is null) throw new ApiException(HttpStatusCode.Unauthorized, "Sign in to use this feature.");
        if (!await EnsureFreshSessionAsync()) throw new ApiException(HttpStatusCode.Unauthorized, "Your session expired. Please sign in again.");

        try
        {
            return await _api.GetJsonAsync(path, _session!.AccessToken);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (!await EnsureFreshSessionAsync(force: true))
                throw new ApiException(HttpStatusCode.Unauthorized, "Your session expired. Please sign in again.");
            return await _api.GetJsonAsync(path, _session!.AccessToken);
        }
    }

    private async Task<JsonDocument> AuthorizedPostAsync(string path, object body)
    {
        if (_session is null) throw new ApiException(HttpStatusCode.Unauthorized, "Sign in to use this feature.");
        if (!await EnsureFreshSessionAsync()) throw new ApiException(HttpStatusCode.Unauthorized, "Your session expired. Please sign in again.");

        try
        {
            return await _api.PostJsonAsync(path, body, _session!.AccessToken);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (!await EnsureFreshSessionAsync(force: true))
                throw new ApiException(HttpStatusCode.Unauthorized, "Your session expired. Please sign in again.");
            return await _api.PostJsonAsync(path, body, _session!.AccessToken);
        }
    }

    private async Task<JsonDocument> AuthorizedPutAsync(string path, object body)
    {
        if (_session is null) throw new ApiException(HttpStatusCode.Unauthorized, "Sign in to use this feature.");
        if (!await EnsureFreshSessionAsync()) throw new ApiException(HttpStatusCode.Unauthorized, "Your session expired. Please sign in again.");
        return await _api.PutJsonAsync(path, body, _session.AccessToken);
    }

    private void StartPresenceTimer()
    {
        if (_session is null) return;
        _presenceTimer.Start();
        _ = SendPresenceHeartbeatAsync();
    }

    private async Task SendPresenceHeartbeatAsync()
    {
        if (_session is null) return;
        try
        {
            using var response = await AuthorizedPutAsync("v1/presence/heartbeat", new { status = "online" });
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingError(ex));
        }
    }

    private async Task<JsonDocument> AuthorizedPatchAsync(string path, object body)
    {
        if (_session is null) throw new ApiException(HttpStatusCode.Unauthorized, "Sign in to use this feature.");
        if (!await EnsureFreshSessionAsync()) throw new ApiException(HttpStatusCode.Unauthorized, "Your session expired. Please sign in again.");
        return await _api.PatchJsonAsync(path, body, _session.AccessToken);
    }

    private async Task<JsonDocument> AuthorizedDeleteAsync(string path)
    {
        if (_session is null) throw new ApiException(HttpStatusCode.Unauthorized, "Sign in to use this feature.");
        if (!await EnsureFreshSessionAsync()) throw new ApiException(HttpStatusCode.Unauthorized, "Your session expired. Please sign in again.");
        return await _api.DeleteJsonAsync(path, _session.AccessToken);
    }

    private void ClearLocalSession()
    {
        _presenceTimer.Stop();
        WindowsSecretStore.Delete(_settings.Environment);
        _session = null;
        UpdateAccountHeader();
    }

    private void NavigateHome()
    {
        PageEyebrow.Text = "1.61ONLINE NETWORK";
        PageTitle.Text = "Welcome back";
        var content = PageColumn();

        var hero = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(34, 30, 34, 30),
            Margin = new Thickness(0, 0, 0, 18),
            Background = new LinearGradientBrush(Color.FromRgb(57, 49, 35), Color.FromRgb(27, 29, 34), 35)
        };
        var heroStack = new StackPanel();
        heroStack.Children.Add(Text("COMMUNITY-POWERED", 11, "#E6B84A", true));
        heroStack.Children.Add(Text("Your community.\nYour next session.", 34, "#FFFFFF", true, 0, 9));
        heroStack.Children.Add(Text("A dedicated desktop home for 1.61ONLINE accounts, friends and community lobby discovery.", 14, "#C7C9CE", false, 0, 16));
        heroStack.Children.Add(MakeAsyncButton("EXPLORE SESSIONS", async () => await ShowSessionsPageAsync(), true));
        hero.Child = heroStack;
        content.Children.Add(hero);

        var grid = new UniformGrid { Columns = 2, Rows = 1, Margin = new Thickness(0, 0, 0, 18) };
        grid.Children.Add(InfoCard("SOCIAL", "Find your people", "Search community profiles, send friend requests and manage accepted friends.",
            MakeAsyncButton("OPEN SOCIAL", async () => await ShowSocialPageAsync())));
        grid.Children.Add(InfoCard("YOUR ACCOUNT", _session is null ? "Make it yours" : "You're signed in",
            _session is null ? "Sign in or create a community account. Tokens are protected by Windows for your user account." : $"Connected as {_session.Email}. Your sign-in is stored securely on this Windows profile.",
            MakeAsyncButton(_session is null ? "SIGN IN" : "VIEW PROFILE", async () => { if (_session is null) ShowAuthPage(false); else await ShowProfilePageAsync(); }, _session is null)));
        content.Children.Add(grid);

        content.Children.Add(Text("BEFORE YOU PLAY", 10, "#E6B84A", true, 0, 9));
        content.Children.Add(InfoCard("IN DEVELOPMENT", "Community services are not the game network",
            "This build connects to the independent 1.61ONLINE API for accounts, social features and lobby records. Session discovery does not synchronize live gameplay; a compatible client adapter and gameplay transport are still separate work.",
            null));
        PageHost.Content = content;
    }

    private static Border InfoCard(string eyebrow, string title, string description, Button? action = null)
    {
        var body = new StackPanel();
        body.Children.Add(Text(eyebrow, 10, "#E6B84A", true));
        body.Children.Add(Text(title, 19, "#F4F4F5", true, 0, 8));
        body.Children.Add(Text(description, 12, "#AEB2BA", false, 0, 16));
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(action);
        }
        return new Border { Background = BrushOf("#1A1C21"), CornerRadius = new CornerRadius(7), Padding = new Thickness(22), Margin = new Thickness(0, 0, 14, 0), Child = body };
    }

    private void ShowAuthPage(bool register)
    {
        _registrationMode = register;
        PageEyebrow.Text = "COMMUNITY ACCOUNT";
        PageTitle.Text = register ? "Create account" : "Sign in";
        var root = PageColumn();
        var card = new Border { Background = BrushOf("#1A1C21"), CornerRadius = new CornerRadius(8), Padding = new Thickness(28), MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left };
        var form = new StackPanel();
        form.Children.Add(Text(register ? "Join the 1.61ONLINE community" : "Welcome back", 21, "#F4F4F5", true, 0, 6));
        form.Children.Add(Text(register ? "Use an email and a unique password (12–128 characters)." : "Sign in with the account registered on the selected community server.", 12, "#AEB2BA", false, 0, 18));
        form.Children.Add(FieldLabel("EMAIL ADDRESS"));
        _authEmail = new TextBox { Width = 420, MaxLength = 254, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(_authEmail);
        form.Children.Add(FieldLabel("PASSWORD"));
        _authPassword = new PasswordBox { Width = 420, MaxLength = 128, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(_authPassword);
        _authMessage = Text("", 12, "#E6B84A", false, 0, 12);
        _authMessage.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(_authMessage);
        form.Children.Add(MakeAsyncButton(register ? "CREATE ACCOUNT" : "SIGN IN", SubmitAuthAsync, true));
        var modeButton = MakeButton(register ? "I ALREADY HAVE AN ACCOUNT" : "CREATE A NEW ACCOUNT", () => ShowAuthPage(!register));
        modeButton.Margin = new Thickness(0, 12, 0, 0);
        modeButton.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(modeButton);
        form.Children.Add(Text("Account credentials are sent only to the configured API. Use HTTPS for a public server. Passwords are never saved by the launcher.", 11, "#777D87", false, 0, 18));
        card.Child = form;
        root.Children.Add(card);
        PageHost.Content = root;
    }

    private async Task SubmitAuthAsync()
    {
        if (_authEmail is null || _authPassword is null || _authMessage is null) return;
        var email = _authEmail.Text.Trim();
        var password = _authPassword.Password;
        _authMessage.Text = "";

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            _authMessage.Text = "Enter a valid email address.";
            return;
        }
        if (password.Length < 12 || password.Length > 128)
        {
            _authMessage.Text = "The community API requires a password between 12 and 128 characters.";
            return;
        }

        try
        {
            using var response = await _api.PostJsonAsync(_registrationMode ? "v1/auth/register" : "v1/auth/login",
                new { email, password });
            var auth = Deserialize<AuthPayload>(response.RootElement);
            if (auth is null || auth.User?.Id is null || string.IsNullOrWhiteSpace(auth.AccessToken) || string.IsNullOrWhiteSpace(auth.RefreshToken))
                throw new InvalidOperationException("The API returned an incomplete sign-in response.");

            _session = new AuthSession
            {
                Email = auth.User.Email ?? email,
                UserId = auth.User.Id,
                AccessToken = auth.AccessToken,
                RefreshToken = auth.RefreshToken,
                AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, auth.ExpiresIn))
            };
            WindowsSecretStore.Save(_settings.Environment, _session);
            UpdateAccountHeader();
            SetStatus("Signed in successfully");
            await RefreshServiceStatusAsync();
            NavigateHome();
        }
        catch (Exception ex)
        {
            _authMessage.Text = UserFacingError(ex);
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            ShowAuthPage(false);
            return;
        }

        var old = _session;
        try
        {
            using var _ = await _api.PostJsonAsync("v1/auth/logout", new { refreshToken = old.RefreshToken });
        }
        catch
        {
            // Local sign-out must still work when the server is unavailable.
        }
        ClearLocalSession();
        SetStatus("Signed out on this device");
        NavigateHome();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => NavigateHome();
    private async void Social_Click(object sender, RoutedEventArgs e) => await ShowSocialPageAsync();
    private async void Sessions_Click(object sender, RoutedEventArgs e) => await ShowSessionsPageAsync();
    private async void Profile_Click(object sender, RoutedEventArgs e) => await ShowProfilePageAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    private async Task ShowProfilePageAsync()
    {
        PageEyebrow.Text = "YOUR COMMUNITY ACCOUNT";
        PageTitle.Text = "Profile";
        var root = PageColumn();
        if (_session is null)
        {
            root.Children.Add(InfoCard("ACCOUNT REQUIRED", "Sign in to see your profile", "Your community profile is scoped to the currently selected Local or Public environment.", MakeButton("SIGN IN", () => ShowAuthPage(false), true)));
            PageHost.Content = root;
            return;
        }

        var cardStack = new StackPanel();
        cardStack.Children.Add(Text("PLAYER PROFILE", 10, "#E6B84A", true));
        cardStack.Children.Add(Text("Loading your community profile…", 13, "#BEC1C7", false, 0, 10));
        var card = new Border { Background = BrushOf("#1A1C21"), CornerRadius = new CornerRadius(7), Padding = new Thickness(24), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left, Child = cardStack };
        root.Children.Add(card);
        PageHost.Content = root;

        try
        {
            using var accountDoc = await AuthorizedGetAsync("v1/client/account");
            var response = accountDoc.RootElement;
            var account = response.TryGetProperty("account", out var a) ? a : default;
            var player = response.TryGetProperty("player", out var p) ? p : default;
            var presence = response.TryGetProperty("presence", out var ps) ? ps : default;

            cardStack.Children.Clear();
            cardStack.Children.Add(Text("PLAYER PROFILE", 10, "#E6B84A", true));
            cardStack.Children.Add(Text(GetString(player, "displayName", "Community Player") ?? "Community Player", 24, "#F4F4F5", true, 0, 5));
            cardStack.Children.Add(Text(GetString(account, "email", _session.Email), 12, "#AEB2BA", false, 0, 16));
            var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 20) };
            stats.Children.Add(Stat("RANK", GetString(player, "rank", "0")));
            stats.Children.Add(Stat("CASH", GetString(player, "cash", "0")));
            stats.Children.Add(Stat("PRESENCE", GetString(presence, "status", "offline") ?? "offline"));
            cardStack.Children.Add(stats);
            cardStack.Children.Add(FieldLabel("DISPLAY NAME"));
            var displayName = new TextBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 32, Text = GetString(player, "displayName") ?? "" };
            cardStack.Children.Add(displayName);
            cardStack.Children.Add(Text("Name must be 3–32 characters and may use letters, numbers, spaces, underscores or hyphens.", 11, "#777D87", false, 0, 12));
            cardStack.Children.Add(MakeAsyncButton("SAVE PROFILE", async () =>
            {
                var value = displayName.Text.Trim();
                if (value.Length < 3 || value.Length > 32) { SetStatus("Display name must be 3–32 characters"); return; }
                using var saved = await AuthorizedPatchAsync("v1/player/profile", new { displayName = value });
                SetStatus("Profile updated");
                await ShowProfilePageAsync();
            }, true));
        }
        catch (Exception ex)
        {
            cardStack.Children.Clear();
            cardStack.Children.Add(Text(UserFacingError(ex), 13, "#E6B84A", false));
            cardStack.Children.Add(MakeAsyncButton("RETRY", async () => await ShowProfilePageAsync()));
        }
    }

    private async Task ShowSocialPageAsync()
    {
        PageEyebrow.Text = "COMMUNITY";
        PageTitle.Text = "Social";
        var root = PageColumn();
        if (_session is null)
        {
            root.Children.Add(InfoCard("ACCOUNT REQUIRED", "Meet the community", "Sign in to search public player names, send friend requests and see accepted friends.", MakeButton("SIGN IN", () => ShowAuthPage(false), true)));
            PageHost.Content = root;
            return;
        }

        var searchBox = new TextBox { Width = 300, MaxLength = 32, VerticalAlignment = VerticalAlignment.Center };
        var results = new StackPanel();
        var friends = new StackPanel();
        var requests = new StackPanel();
        var searchPanel = new StackPanel();
        searchPanel.Children.Add(Text("Search by display name", 18, "#F4F4F5", true, 0, 3));
        searchPanel.Children.Add(Text("Enter at least two characters. Search results never reveal email addresses.", 12, "#AEB2BA", false, 0, 8));
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal };
        searchBox.Margin = new Thickness(0, 6, 10, 12);
        searchRow.Children.Add(searchBox);
        searchRow.Children.Add(MakeAsyncButton("SEARCH", async () => await SearchPlayersAsync(searchBox.Text, results), true));
        searchPanel.Children.Add(searchRow);
        searchPanel.Children.Add(results);
        root.Children.Add(Card(searchPanel));

        var sections = new UniformGrid { Columns = 2, Margin = new Thickness(0, 18, 0, 0) };
        var friendStack = new StackPanel();
        friendStack.Children.Add(Text("FRIENDS", 10, "#E6B84A", true, 0, 10));
        friendStack.Children.Add(friends);
        var requestStack = new StackPanel();
        requestStack.Children.Add(Text("INCOMING REQUESTS", 10, "#E6B84A", true, 0, 10));
        requestStack.Children.Add(requests);
        sections.Children.Add(Card(friendStack));
        sections.Children.Add(Card(requestStack));
        root.Children.Add(sections);
        PageHost.Content = root;
        await LoadSocialListsAsync(friends, requests);
    }

    private async Task SearchPlayersAsync(string query, Panel results)
    {
        results.Children.Clear();
        if (query.Trim().Length < 2)
        {
            results.Children.Add(Text("Type at least two characters to search.", 12, "#E6B84A"));
            return;
        }
        using var document = await AuthorizedGetAsync($"v1/social/players?query={Uri.EscapeDataString(query.Trim())}");
        if (!document.RootElement.TryGetProperty("players", out var players) || players.GetArrayLength() == 0)
        {
            results.Children.Add(Text("No matching players found.", 12, "#AEB2BA"));
            return;
        }

        foreach (var player in players.EnumerateArray())
        {
            var userId = GetString(player, "userId");
            var name = GetString(player, "displayName", "Player") ?? "Player";
            var status = GetString(player, "status", "offline") ?? "offline";
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var add = MakeAsyncButton("ADD FRIEND", async () =>
                {
                    using var sent = await AuthorizedPostAsync("v1/social/friend-requests", new { targetUserId = userId });
                    SetStatus($"Friend request sent to {name}");
                    await ShowSocialPageAsync();
                });
                DockPanel.SetDock(add, Dock.Right);
                row.Children.Add(add);
            }
            row.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { Text(name, 13, "#F4F4F5", true), Text(status.ToUpperInvariant(), 10, "#8D929B") }
            });
            results.Children.Add(ThinRow(row));
        }
    }

    private async Task LoadSocialListsAsync(Panel friendsPanel, Panel requestsPanel)
    {
        friendsPanel.Children.Clear();
        requestsPanel.Children.Clear();
        try
        {
            using var friendsDoc = await AuthorizedGetAsync("v1/social/friends");
            if (friendsDoc.RootElement.TryGetProperty("friends", out var friendArray) && friendArray.GetArrayLength() > 0)
            {
                foreach (var friend in friendArray.EnumerateArray())
                {
                    friendsPanel.Children.Add(ThinRow(new StackPanel
                    {
                        Children =
                        {
                            Text(GetString(friend, "displayName", "Player") ?? "Player", 13, "#F4F4F5", true),
                            Text((GetString(friend, "status", "offline") ?? "offline").ToUpperInvariant(), 10, "#8D929B")
                        }
                    }));
                }
            }
            else friendsPanel.Children.Add(Text("No friends yet. Search for a player above.", 12, "#8D929B"));

            using var requestsDoc = await AuthorizedGetAsync("v1/social/friend-requests?direction=incoming");
            if (requestsDoc.RootElement.TryGetProperty("requests", out var requestArray) && requestArray.GetArrayLength() > 0)
            {
                foreach (var request in requestArray.EnumerateArray())
                {
                    var userId = GetString(request, "userId");
                    var name = GetString(request, "displayName", "Player") ?? "Player";
                    var row = new StackPanel();
                    row.Children.Add(Text(name, 13, "#F4F4F5", true, 0, 8));
                    if (!string.IsNullOrWhiteSpace(userId))
                    {
                        row.Children.Add(MakeAsyncButton("ACCEPT", async () =>
                        {
                            using var accepted = await AuthorizedPostAsync($"v1/social/friend-requests/{Uri.EscapeDataString(userId)}/accept", new { });
                            SetStatus($"You and {name} are now friends");
                            await ShowSocialPageAsync();
                        }, true));
                    }
                    requestsPanel.Children.Add(ThinRow(row));
                }
            }
            else requestsPanel.Children.Add(Text("No pending requests.", 12, "#8D929B"));
        }
        catch (Exception ex)
        {
            friendsPanel.Children.Add(Text(UserFacingError(ex), 12, "#E6B84A"));
        }
    }

    private async Task ShowSessionsPageAsync()
    {
        PageEyebrow.Text = "PLAY TOGETHER";
        PageTitle.Text = "Community sessions";
        var root = PageColumn();
        root.Children.Add(InfoCard("LOBBY DISCOVERY", "Find a community lobby",
            "These are control-plane session records from the 1.61ONLINE API. Joining updates your community membership only; it does not connect game clients or synchronize live gameplay."));
        if (_session is null)
        {
            root.Children.Add(InfoCard("ACCOUNT REQUIRED", "Sign in to view sessions", "Public lobby records are visible to signed-in community accounts.", MakeButton("SIGN IN", () => ShowAuthPage(false), true)));
            PageHost.Content = root;
            return;
        }

        var createCard = new StackPanel();
        createCard.Children.Add(Text("CREATE A PUBLIC SESSION", 10, "#E6B84A", true, 0, 6));
        var nameBox = new TextBox { Text = "Freemode community session", MaxLength = 48, Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        createCard.Children.Add(FieldLabel("SESSION NAME"));
        createCard.Children.Add(nameBox);
        createCard.Children.Add(Text("Region: us-east · Mode: freemode · Capacity: 16 players", 11, "#AEB2BA", false, 0, 10));
        createCard.Children.Add(MakeAsyncButton("CREATE SESSION", async () =>
        {
            var name = nameBox.Text.Trim();
            if (name.Length < 3 || name.Length > 48) { SetStatus("Session name must be 3–48 characters"); return; }
            using var created = await AuthorizedPostAsync("v1/sessions", new
            {
                name,
                region = "us-east",
                gameMode = "freemode",
                visibility = "public",
                maxPlayers = 16
            });
            SetStatus("Community session created (lobby record only)");
            await ShowSessionsPageAsync();
        }, true));
        root.Children.Add(Card(createCard));

        var listPanel = new StackPanel();
        listPanel.Children.Add(Text("OPEN PUBLIC SESSIONS", 10, "#E6B84A", true, 0, 8));
        listPanel.Children.Add(Text("Loading sessions…", 12, "#AEB2BA"));
        root.Children.Add(Card(listPanel));
        PageHost.Content = root;

        try
        {
            using var document = await AuthorizedGetAsync("v1/sessions");
            listPanel.Children.Clear();
            listPanel.Children.Add(Text("OPEN PUBLIC SESSIONS", 10, "#E6B84A", true, 0, 8));
            var sessions = document.RootElement.TryGetProperty("sessions", out var array) ? array : default;
            if (sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() == 0)
            {
                listPanel.Children.Add(Text("No open public sessions yet. Create one above to start a lobby record.", 12, "#AEB2BA"));
                return;
            }
            foreach (var session in sessions.EnumerateArray())
            {
                var id = GetString(session, "id");
                var name = GetString(session, "name", "Community session") ?? "Community session";
                var region = GetString(session, "region", "unknown") ?? "unknown";
                var mode = GetString(session, "gameMode", "unknown") ?? "unknown";
                var count = GetString(session, "playerCount", "0") ?? "0";
                var capacity = GetString(session, "maxPlayers", "16") ?? "16";
                var row = new DockPanel { LastChildFill = true };
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var join = MakeAsyncButton("JOIN LOBBY", async () =>
                    {
                        using var joined = await AuthorizedPostAsync($"v1/sessions/{Uri.EscapeDataString(id)}/join", new { });
                        SetStatus("Joined community lobby record; no game connection was started");
                        await ShowSessionsPageAsync();
                    }, true);
                    DockPanel.SetDock(join, Dock.Right);
                    row.Children.Add(join);
                }
                row.Children.Add(new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        Text(name, 14, "#F4F4F5", true),
                        Text($"{mode} · {region} · {count}/{capacity} players · community record", 11, "#AEB2BA")
                    }
                });
                listPanel.Children.Add(ThinRow(row));
            }
        }
        catch (Exception ex)
        {
            listPanel.Children.Clear();
            listPanel.Children.Add(Text(UserFacingError(ex), 12, "#E6B84A"));
        }
    }

    private void ShowSettingsPage()
    {
        PageEyebrow.Text = "PREFERENCES";
        PageTitle.Text = "Settings";
        var root = PageColumn();
        var form = new StackPanel();
        form.Children.Add(Text("ENVIRONMENT", 10, "#E6B84A", true, 0, 5));
        form.Children.Add(Text("Switch between your local API and a deployed public server. Sign-in sessions are stored separately for each environment.", 12, "#AEB2BA", false, 0, 8));
        var environmentPicker = new ComboBox { Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        environmentPicker.Items.Add("Local Development");
        environmentPicker.Items.Add("Public Server");
        environmentPicker.SelectedIndex = _settings.Environment == "Public" ? 1 : 0;
        form.Children.Add(environmentPicker);

        form.Children.Add(FieldLabel("LOCAL API URL"));
        var localUrl = new TextBox { Text = _settings.LocalApiUrl, Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(localUrl);
        form.Children.Add(Text("Default for the backend you are running on this PC: http://127.0.0.1:3000/", 11, "#777D87", false, 0, 8));

        form.Children.Add(FieldLabel("PUBLIC API URL"));
        var publicUrl = new TextBox { Text = _settings.PublicApiUrl, Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(publicUrl);
        form.Children.Add(Text("Enter your deployed HTTPS API URL when you are ready to switch. The launcher does not invent or deploy a public server for you.", 11, "#777D87", false, 0, 8));

        form.Children.Add(FieldLabel("GTA V INSTALL FOLDER (OPTIONAL)"));
        var gameRow = new DockPanel { LastChildFill = true };
        var gamePath = new TextBox { Text = _settings.GameInstallPath, Margin = new Thickness(0, 6, 8, 12) };
        var browse = MakeButton("BROWSE", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Select your GTA V installation folder", Multiselect = false };
            if (Directory.Exists(gamePath.Text)) dialog.InitialDirectory = gamePath.Text;
            if (dialog.ShowDialog(this) == true) gamePath.Text = dialog.FolderName;
        });
        DockPanel.SetDock(browse, Dock.Right);
        gameRow.Children.Add(browse);
        gameRow.Children.Add(gamePath);
        form.Children.Add(gameRow);

        form.Children.Add(Text("Security: public mode only accepts HTTPS. Passwords are never saved. Session tokens are encrypted using Windows DPAPI for the current Windows user. A game path is saved only; launching or modifying the game is not enabled in this foundation.", 11, "#8D929B", false, 0, 14));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MakeAsyncButton("SAVE & APPLY", async () =>
        {
            _settings.Environment = environmentPicker.SelectedIndex == 1 ? "Public" : "Local";
            _settings.LocalApiUrl = localUrl.Text.Trim();
            _settings.PublicApiUrl = publicUrl.Text.Trim();
            _settings.GameInstallPath = gamePath.Text.Trim();

            var validation = _settings.ValidateActiveEndpoint();
            if (!string.IsNullOrEmpty(validation))
            {
                MessageBox.Show(this, validation, "Check API URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _settings.Save();
                var newClient = new CommunityApiClient(_settings.ActiveApiUrl);
                var oldClient = _api;
                _api = newClient;
                oldClient.Dispose();
                _session = WindowsSecretStore.Load(_settings.Environment);
                if (_session is null) _presenceTimer.Stop(); else StartPresenceTimer();
                UpdateEnvironmentHeader();
                UpdateAccountHeader();
                await RefreshServiceStatusAsync();
                NavigateHome();
                SetStatus(_settings.Environment == "Local" ? "Local environment applied" : "Public environment applied");
            }
            catch (Exception ex)
            {
                SetStatus(UserFacingError(ex));
            }
        }, true));
        buttons.Children.Add(MakeAsyncButton("TEST CONNECTION", async () => await RefreshServiceStatusAsync()));
        buttons.Children[1].SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
        form.Children.Add(buttons);

        var card = Card(form);
        card.MaxWidth = 760;
        root.Children.Add(card);
        root.Children.Add(Text("Tip: keep the backend's .env file private. Only the public API base URL belongs in launcher settings—never put JWT secrets or database passwords in the launcher.", 11, "#777D87", false, 0, 16));
        PageHost.Content = root;
    }

    private static Border Stat(string label, string value)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(label, 9, "#8D929B", true));
        stack.Children.Add(Text(value, 14, "#F4F4F5", true, 0, 8));
        return new Border { Background = BrushOf("#22252B"), CornerRadius = new CornerRadius(5), Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 0), Child = stack };
    }

    private static Border Card(UIElement child) =>
        new() { Background = BrushOf("#1A1C21"), CornerRadius = new CornerRadius(7), Padding = new Thickness(22), Margin = new Thickness(0, 0, 12, 12), Child = child };

    private static Border ThinRow(UIElement child) =>
        new() { Background = BrushOf("#22252B"), CornerRadius = new CornerRadius(5), Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 4), Child = child };

    private static StackPanel PageColumn() => new() { Margin = new Thickness(0, 0, 0, 22), Orientation = Orientation.Vertical };

    private static TextBlock FieldLabel(string value) => Text(value, 10, "#8D929B", true, 0, 2);

    private static TextBlock Text(string value, double size = 13, string color = "#BEC1C7", bool bold = false, double top = 0, double bottom = 0)
    {
        var text = new TextBlock
        {
            Text = value,
            FontSize = size,
            Foreground = BrushOf(color),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, bottom)
        };
        return text;
    }

    private Button MakeButton(string caption, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = caption,
            Background = BrushOf(primary ? "#E6B84A" : "#292D35"),
            Foreground = BrushOf(primary ? "#171717" : "#F4F4F5"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => action();
        return button;
    }

    private Button MakeAsyncButton(string caption, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Content = caption,
            Background = BrushOf(primary ? "#E6B84A" : "#292D35"),
            Foreground = BrushOf(primary ? "#171717" : "#F4F4F5"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += async (_, _) =>
        {
            if (!button.IsEnabled) return;
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { SetStatus(UserFacingError(ex)); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static SolidColorBrush BrushOf(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private static string? GetString(JsonElement element, string name, string? fallback = null)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => fallback
            };
        }
        return fallback;
    }

    private static T? Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);

    private sealed class AuthPayload
    {
        public AuthUser? User { get; set; }
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
    }

    private sealed class AuthUser
    {
        public string? Id { get; set; }
        public string? Email { get; set; }
    }
}
