using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SlimeHelper
{
    public partial class MainWindow : Window
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string statusFilePath;
        private readonly DispatcherTimer checkTimer;
        private bool isInteracting;
        private readonly Random rng = new();
        private string lastStatus = "";
        private double currentVolume = 0.5;
        private readonly MediaPlayer mediaPlayer = new();
        private Point startWindowPos;
        private string currentSkin = "Default";
        private readonly string settingsPath = Path.Combine(Path.GetTempPath(), "slime_settings.json");

        private readonly ActivityWatcher _activityWatcher = new();
        private readonly CodeWatcherService _codeWatcher = new();
        private FileSystemWatcher? _cliCommandWatcher;

        // Animation State Machine variabler
        private CancellationTokenSource? _animationCts;
        private readonly Dictionary<string, AnimationProfile> _animationProfiles = [];
        private string _currentPlayingState = "";

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            LoadAnimations();
            var settings = LoadFullSettings();

            statusFilePath = Path.Combine(Path.GetTempPath(), "slime_status.txt");

            checkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            checkTimer.Tick += CheckStatus;
            checkTimer.Start();

            _activityWatcher.OnReaction += (status, msg) => ShowSlimeReaction(status, msg);
            _codeWatcher.OnReaction += (status, msg) => ShowSlimeReaction(status, msg);

            if (!string.IsNullOrEmpty(settings.ReposRootPath))
            {
                _codeWatcher.Start(settings.ReposRootPath);
            }

            // Drag function
            MouseLeftButtonDown += (s, e) =>
            {
                startWindowPos = new Point(Left, Top);
                DragMove();
            };

            MouseLeftButtonUp += (s, e) =>
            {
                double distanceMoved = Math.Abs(Left - startWindowPos.X)
                                     + Math.Abs(Top - startWindowPos.Y);

                if (distanceMoved < 5)
                {
                    PokeSlime();
                }
            };

            // Open menu
            MouseRightButtonUp += (s, e) =>
            {
                if (ContextMenu is not null)
                {
                    ContextMenu.IsOpen = true;
                }
            };

            SetupCommandWatcher();
            ShowSlimeReaction("IDLE", "");
            RunStatusCheck();
        }

        // --- SLIME ANIMATIONS LOGIC ---

        private void LoadAnimations()
        {
            _animationProfiles.Clear();
            string basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images", currentSkin);

            if (!Directory.Exists(basePath)) return;

            foreach (var stateDir in Directory.GetDirectories(basePath))
            {
                string stateName = new DirectoryInfo(stateDir).Name.ToUpperInvariant();

                var files = Directory.GetFiles(stateDir, "*.png").OrderBy(f => f).ToList();
                if (files.Count == 0) continue;

                var profile = new AnimationProfile { Frames = files };

                if (stateName == "IDLE")
                {
                    profile.FrameDelayMs = 200;
                    profile.LoopDelayMs = 3000;

                    if (files.Count >= 3)
                    {
                        var pingPong = new List<string>(files);
                        for (int i = files.Count - 2; i > 0; i--)
                        {
                            pingPong.Add(files[i]);
                        }
                        profile.Frames = pingPong;
                    }
                }
                else if (stateName == "BLINK")
                {
                    profile.FrameDelayMs = 150;
                    profile.LoopDelayMs = 0;
                }
                else
                {
                    profile.FrameDelayMs = 150;
                    profile.LoopDelayMs = 0;
                }

                _animationProfiles[stateName] = profile;
            }
        }

        private async Task PlayAnimationLoop(string stateKey)
        {
            stateKey = stateKey.ToUpperInvariant();

            if (!_animationProfiles.ContainsKey(stateKey))
            {
                if (_animationProfiles.ContainsKey("IDLE")) stateKey = "IDLE";
                else
                {
                    // Fallback om inga mappar hittas alls
                    Dispatcher.Invoke(() =>
                    {
                        string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images", "slime_idle.png");
                        if (File.Exists(fallback)) UpdateImage(fallback, false);
                    });
                    return;
                }
            }

            if (_currentPlayingState == stateKey && _animationCts != null && !_animationCts.IsCancellationRequested)
            {
                return;
            }

            _currentPlayingState = stateKey;

            _animationCts?.Cancel();
            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;

            var profile = _animationProfiles[stateKey];
            if (profile.Frames.Count == 0) return;

            bool isFirstFrame = true;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var framePath in profile.Frames)
                    {
                        token.ThrowIfCancellationRequested();

                        Dispatcher.Invoke(() => UpdateImage(framePath, isFirstFrame));
                        isFirstFrame = false;

                        await Task.Delay(profile.FrameDelayMs, token);
                    }

                    if (stateKey == "IDLE")
                    {
                        await Task.Delay(profile.LoopDelayMs, token);

                        if (!isInteracting && rng.Next(0, 10) < 3 && _animationProfiles.TryGetValue("BLINK", out var blinkProfile) && blinkProfile.Frames.Count > 0)
                        {
                            foreach (var blinkFrame in blinkProfile.Frames)
                            {
                                token.ThrowIfCancellationRequested();
                                Dispatcher.Invoke(() => UpdateImage(blinkFrame, false));
                                await Task.Delay(blinkProfile.FrameDelayMs, token);
                            }

                            await Task.Delay(500, token);
                        }
                    }
                    else if (profile.LoopDelayMs > 0)
                    {
                        await Task.Delay(profile.LoopDelayMs, token);
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void UpdateImage(string imagePath, bool useFade = true)
        {
            if (!File.Exists(imagePath)) return;

            if (SlimeImage.Source is BitmapImage currentBitmap &&
                currentBitmap.UriSource.AbsolutePath.Equals(new Uri(imagePath).AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            if (useFade)
            {
                var fadeOut = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(150)
                };

                fadeOut.Completed += (s, e) =>
                {
                    SlimeImage.Source = bitmap;
                    var fadeIn = new DoubleAnimation
                    {
                        From = 0.0,
                        To = 0.6,
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    SlimeImage.BeginAnimation(OpacityProperty, fadeIn);
                };

                SlimeImage.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                SlimeImage.BeginAnimation(OpacityProperty, null);
                SlimeImage.Opacity = 0.6;
                SlimeImage.Source = bitmap;
            }
        }

        // --- APP LOGIC ---

        private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            currentVolume = e.NewValue;
        }

        private void CloseApp(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void PlaySounds(string soundFile)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds", soundFile);
                if (File.Exists(path))
                {
                    mediaPlayer.Open(new Uri(path));
                    mediaPlayer.Volume = currentVolume;
                    mediaPlayer.Play();
                }
            }
            catch { }
        }

        private void PokeSlime()
        {
            if (isInteracting) return;
            isInteracting = true;

            PlaySounds("Poke.wav");

            string[] pokePhrase = currentSkin switch
            {
                "Green" =>
                [
                    "Don't poke me!", "You'll get green goo on your cursor", "Wobble, Wobble",
                    "Do I look like a jelly shot?", "I'm melting! I'm melting!", "Maybe we should go back to coding?",
                    "Remember to drink water!", "JS or TS? That the question...", "POKE-E-MON", "Slime!", "Why are you poking me?!?!"
                ],
                "Pink" =>
                [
                    "Fluffy!", "Pink and cute", "Wanna take a break?", "Flowers and butterflies",
                    "Don't poke me so hard!", "My antennas", "Bubble, Bubble", "I'm just chilling here!",
                    "Your code is beautiful!", "Did we fix that bug?", "We should use a pink theme!"
                ],
                "Girl" =>
                [
                    "Don't poke me!", "Cute and Squishy", "Wanna take a break?", "Hey, cut it out!",
                    "You make me wooble", "I'm gonna melt into the CPU", "Be nice mister", "I'm just chilling here!",
                    "Your code is beautiful!", "Did we fix that bug?", "Maybe try to get some work done?"
                ],
                _ =>
                [
                    "Don't poke me!", "Get back to coding!", "Careful! I'm squishy...", "Is it time for a break?",
                    "You should focus on your code", "Hey! Don't do that!", "I want cake...", "Squish!",
                    "Maybe just one more poke?", "Have you saved and commited your code?", "Slime is doing slime stuff"
                ]
            };

            int index = rng.Next(pokePhrase.Length);
            SpeechText.Text = pokePhrase[index];
            SpeechText.Foreground = Brushes.Black;
            SpeechBubble.Visibility = Visibility.Visible;

            ShowSlimeReaction("POKE", "");

            var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            resetTimer.Tick += (s, args) =>
            {
                isInteracting = false;
                SpeechBubble.Visibility = Visibility.Collapsed;
                RunStatusCheck();
                resetTimer.Stop();
            };
            resetTimer.Start();
        }

        private void CheckStatus(object? sender, EventArgs e)
        {
            RunStatusCheck();
        }

        private void RunStatusCheck()
        {
            if (isInteracting) return;

            string commandFile = Path.Combine(Path.GetTempPath(), "slime_command.txt");
            if (File.Exists(commandFile))
            {
                try
                {
                    string command = File.ReadAllText(commandFile).Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        if (command == "OPEN_NOTES")
                        {
                            File.WriteAllText(commandFile, "");
                            TriggerOpenNotes();
                            return;
                        }
                        else if (command.StartsWith("ASK_AI:", StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(commandFile, "");
                            string prompt = command[7..];
                            ProcessAiRequest(prompt);
                            return;
                        }
                    }
                }
                catch { }
            }

            if (!File.Exists(statusFilePath)) return;

            try
            {
                string jsonContent = File.ReadAllText(statusFilePath).Trim();
                var data = JsonSerializer.Deserialize<SlimeData>(jsonContent);
                if (data is null) return;

                bool statusChanged = data.status != lastStatus;

                if (statusChanged)
                {
                    switch (data.status)
                    {
                        case "ERROR":
                        case "WARNING":
                            PlaySounds("Warning.wav"); break;
                        case "BREAK":
                            PlaySounds("Poke.wav"); break;
                        case "IDLE":
                            if (lastStatus == "AFK") PlaySounds("Poke.wav");
                            else if (lastStatus is "ERROR" or "WARNING") PlaySounds("Idle.wav");
                            break;
                    }
                    lastStatus = data.status;
                }

                if (!string.IsNullOrEmpty(data.text))
                {
                    SpeechText.Text = data.text;
                    SpeechBubble.Visibility = Visibility.Visible;

                    if (data.status == "IDLE")
                    {
                        var now = DateTime.Now;

                        if (now.Hour is >= 23 or < 5)
                        {
                            SpeechText.Text = "It's late. Slime is tired...";
                            data.status = "TIRED";
                        }
                        else if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 15)
                        {
                            SpeechText.Text = "It's Friday! Friday! Yey!";
                            data.status = "STREAK";
                        }
                        else if (now.DayOfWeek == DayOfWeek.Monday && now.Hour < 9)
                        {
                            SpeechText.Text = "Monday... need coffee... ";
                            data.status = "TIRED";
                        }
                        else if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                        {
                            if (rng.Next(0, 10) == 0)
                            {
                                SpeechText.Text = "Working on the weekend? Really?";
                            }
                        }
                    }

                    SpeechText.Foreground = data.status switch
                    {
                        "ERROR" => Brushes.Red,
                        "WARNING" => Brushes.DarkOrange,
                        _ => Brushes.Black
                    };
                }
                else
                {
                    SpeechBubble.Visibility = Visibility.Collapsed;
                }

                if (statusChanged)
                {
                    ShowSlimeReaction(data.status, "");
                }
            }
            catch { }
        }

        private void ChangeSkin(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item)
            {
                currentSkin = item.Tag?.ToString() ?? "Default";
                SaveSettings();

                string greeting = currentSkin switch
                {
                    "Green" => "Goo-morning! Let's melt some bugs.",
                    "Pink" => "Fabulous! I feel... different.",
                    "Girl" => "I'm ready! Let's go!",
                    _ => "I'm blue dabidi dabida."
                };

                LoadAnimations();
                ShowSlimeReaction("IDLE", greeting);
                RunStatusCheck();

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, args) =>
                {
                    SpeechBubble.Visibility = Visibility.Collapsed;
                    timer.Stop();
                };
                timer.Start();
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new SlimeSettings { CurrentSkin = currentSkin };
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(settingsPath, json);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonSerializer.Deserialize<SlimeSettings>(json);
                    currentSkin = settings?.CurrentSkin ?? "Default";

                    string provider = settings?.SelectedProvider ?? "Gemini";
                    GeminiCheck.IsChecked = (provider == "Gemini");
                    ClaudeCheck.IsChecked = (provider == "Claude");
                }
            }
            catch { currentSkin = "Default"; }
        }

        private void OnViewNotesClick(object sender, RoutedEventArgs e)
        {
            TriggerOpenNotes();
        }

        private void TriggerOpenNotes()
        {
            try
            {
                string commandFile = Path.Combine(Path.GetTempPath(), "slime_command.txt");
                File.WriteAllText(commandFile, "OPEN_NOTES");

                SpeechText.Text = "Opening your notes";
                SpeechText.Foreground = Brushes.Black;
                SpeechBubble.Visibility = Visibility.Visible;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, args) =>
                {
                    SpeechBubble.Visibility = Visibility.Collapsed;
                    RunStatusCheck();
                    timer.Stop();
                };
                timer.Start();
            }
            catch
            {
                SpeechText.Text = "Oops! Couldn't open notes.";
                SpeechBubble.Visibility = Visibility.Visible;
            }
        }

        private void OnSetGeminiKeyClick(object sender, RoutedEventArgs e)
        {
            var currentSettings = LoadFullSettings();
            string key = SlimeInputDialog.Show("Slime Brain Configuration", "Enter your Gemini API Key:", currentSettings.GeminiKey);

            if (!string.IsNullOrWhiteSpace(key) && key != "Enter your Key here!")
            {
                SaveGeminiKey_Click(key);
            }
        }

        private void SaveGeminiKey_Click(string newKey)
        {
            var settings = LoadFullSettings();
            settings.GeminiKey = newKey;
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(settingsPath, json);

            SpeechText.Text = "Gemini key saved to my settings! ✨";
            SpeechBubble.Visibility = Visibility.Visible;
            PlaySounds("Idle.wav");
        }

        private void InstallCliToPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string sourceExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "slime.exe");
                if (!File.Exists(sourceExe))
                {
                    ShowSlimeReaction("ERROR", "slime.exe was not found in the application directory.");
                    return;
                }

                // 1. Skapa en lokal mapp för CLI-verktyget
                string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlimeHelper", "bin");
                Directory.CreateDirectory(targetDir);

                string targetExe = Path.Combine(targetDir, "slime.exe");
                File.Copy(sourceExe, targetExe, true);

                // 2. Hämta nuvarande User PATH och lägg till mappen om den saknas
                string currentPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
                var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

                if (!paths.Any(p => string.Equals(p.Trim(), targetDir, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(targetDir);
                    string newPath = string.Join(";", paths);
                    Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.User);
                }

                ShowSlimeReaction("FUNNY", "CLI installed! Restart your terminal and run 'slime help'.");
            }
            catch (Exception ex)
            {
                ShowSlimeReaction("ERROR", $"Failed to install CLI: {ex.Message}");
            }
        }

        private async void ProcessAiRequest(string prompt)
        {
            isInteracting = true;
            string response = "";

            SpeechText.Text = "Hmm... let me think...";
            SpeechText.Foreground = Brushes.Black;
            SpeechBubble.Visibility = Visibility.Visible;

            ShowSlimeReaction("FUNNY", "");

            try
            {
                var settings = LoadFullSettings();
                IAiProvider provider = GetAiProvider(settings.SelectedProvider);

                string apiKey = (settings.SelectedProvider == "Claude") ? settings.ClaudeKey : settings.GeminiKey;

                if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "Enter your Key here!")
                {
                    throw new InvalidOperationException($"Missing API Key for {settings.SelectedProvider}. Check settings!");
                }

                string context = ContextManager.BuildFullContext(settings, prompt, lastStatus);
                string fullQuery = string.IsNullOrWhiteSpace(context)
                    ? prompt
                    : $"[Current Workspace Context]\n{context}\n\n[User Prompt]\n{prompt}";

                response = await AiService.AskSlime(fullQuery, provider, apiKey);

                SpeechText.Text = response;
                ShowSlimeReaction("IDLE", "");
                PlaySounds("Idle.wav");
            }
            catch (Exception ex)
            {
                SpeechText.Text = $"Brain freeze! {ex.Message}";
                ShowSlimeReaction("ERROR", "");
                SpeechText.Foreground = Brushes.Red;
                response = SpeechText.Text;
            }

            int displayTime = Math.Max(4, response.Length / 50);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(displayTime) };
            timer.Tick += (s, args) =>
            {
                isInteracting = false;
                SpeechBubble.Visibility = Visibility.Collapsed;
                RunStatusCheck();
                timer.Stop();
            };
            timer.Start();
        }

        public static IAiProvider GetAiProvider(string providerName)
        {
            return providerName.ToLowerInvariant() switch
            {
                "claude" => new ClaudeProvider(),
                _ => new GeminiProvider()
            };
        }

        private void OnProviderChangeClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item)
            {
                string selectedProvider = item.Tag?.ToString() ?? "Gemini";
                var config = LoadFullSettings();
                config.SelectedProvider = selectedProvider;
                string json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(settingsPath, json);

                GeminiCheck.IsChecked = (selectedProvider == "Gemini");
                ClaudeCheck.IsChecked = (selectedProvider == "Claude");

                SpeechText.Text = $"Brain switched to {selectedProvider}!";
                SpeechBubble.Visibility = Visibility.Visible;
                ShowSlimeReaction("FUNNY", "");
            }
        }

        private void OnSetClaudeKeyClick(object sender, RoutedEventArgs e)
        {
            var currentSettings = LoadFullSettings();
            string key = SlimeInputDialog.Show("Slime Brain Configuration", "Enter your Claude API Key:", currentSettings.ClaudeKey);
            if (!string.IsNullOrEmpty(key) && key != "Enter your Key here!")
            {
                SaveClaudeKey(key);
            }
        }

        private void SaveClaudeKey(string key)
        {
            var config = LoadFullSettings();
            config.ClaudeKey = key;
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(settingsPath, json);

            SpeechText.Text = "Claude is ready to think!";
            SpeechBubble.Visibility = Visibility.Visible;
        }

        private void OnSetObsidianVaultClick(object sender, RoutedEventArgs e)
        {
            var currentSettings = LoadFullSettings();
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Obsidian Vault folder",
                InitialDirectory = Directory.Exists(currentSettings.ObsidianVaultPath)
                    ? currentSettings.ObsidianVaultPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (dialog.ShowDialog() == true)
            {
                currentSettings.ObsidianVaultPath = dialog.FolderName;
                SaveFullSettings(currentSettings);

                SpeechText.Text = "Obsidian Vault connected! 📓✨";
                SpeechText.Foreground = Brushes.Black;
                SpeechBubble.Visibility = Visibility.Visible;
                ShowSlimeReaction("FUNNY", "");
                PlaySounds("Idle.wav");
            }
        }

        private void ShowSlimeReaction(string status, string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrEmpty(message))
                {
                    SpeechText.Text = message;
                    SpeechBubble.Visibility = Visibility.Visible;
                }

                if (isInteracting && status != "ERROR") return;

                _ = PlayAnimationLoop(status);
            });
        }

        private void OnSetReposPathClick(object sender, RoutedEventArgs e)
        {
            var currentSettings = LoadFullSettings();
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Git Repositories root folder",
                InitialDirectory = Directory.Exists(currentSettings.ReposRootPath)
                    ? currentSettings.ReposRootPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dialog.ShowDialog() == true)
            {
                currentSettings.ReposRootPath = dialog.FolderName;
                SaveFullSettings(currentSettings);

                _codeWatcher.Start(currentSettings.ReposRootPath);

                SpeechText.Text = "Git Repositories folder linked! 📁🚀";
                SpeechText.Foreground = Brushes.Black;
                SpeechBubble.Visibility = Visibility.Visible;
                ShowSlimeReaction("FUNNY", "");
                PlaySounds("Idle.wav");
            }
        }

        private void SaveFullSettings(SlimeSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(settingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not save settings: {ex.Message}");
            }
        }

        private SlimeSettings LoadFullSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    return JsonSerializer.Deserialize<SlimeSettings>(json) ?? new SlimeSettings();
                }
            }
            catch { }
            return new SlimeSettings();
        }

        private void SetupCommandWatcher()
        {
            string commandFile = Path.Combine(Path.GetTempPath(), "slime_command.json");
            if (!File.Exists(commandFile)) File.WriteAllText(commandFile, "");

            _cliCommandWatcher = new FileSystemWatcher(Path.GetTempPath(), "slime_command.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _cliCommandWatcher.Changed += async (s, e) =>
            {
                try
                {
                    _cliCommandWatcher.EnableRaisingEvents = false;
                    await Task.Delay(100);
                    string json = File.ReadAllText(commandFile);

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("Prompt", out var promptProp))
                        {
                            string prompt = promptProp.GetString() ?? "";

                            if (prompt.StartsWith("SET_SKIN:", StringComparison.OrdinalIgnoreCase))
                            {
                                string newSkin = prompt[9..].Trim();
                                if (newSkin.Length > 0) newSkin = string.Concat(char.ToUpperInvariant(newSkin[0]), newSkin.AsSpan(1).ToString().ToLowerInvariant());

                                Dispatcher.Invoke(() =>
                                {
                                    currentSkin = newSkin;
                                    var settings = LoadFullSettings();
                                    settings.CurrentSkin = currentSkin;
                                    SaveFullSettings(settings);
                                    LoadAnimations();
                                    ShowSlimeReaction("FUNNY", $"Changed skin to {currentSkin}!");
                                });
                            }
                            else if (!string.IsNullOrEmpty(prompt))
                            {
                                Dispatcher.Invoke(() => ProcessAiRequest(prompt));
                            }
                        }
                        File.WriteAllText(commandFile, "");
                    }
                }
                catch { }
                finally
                {
                    _cliCommandWatcher.EnableRaisingEvents = true;
                }
            };

            _cliCommandWatcher.EnableRaisingEvents = true;
        }
    }

    public class SlimeData
    {
        public string status { get; set; } = "";
        public string text { get; set; } = "";
    }

    public class SlimeSettings
    {
        public string CurrentSkin { get; set; } = "Default";
        public string SelectedProvider { get; set; } = "Gemini";
        public string GeminiKey { get; set; } = "";
        public string ClaudeKey { get; set; } = "";
        public string ObsidianVaultPath { get; set; } = "";
        public string ReposRootPath { get; set; } = "";
        public bool AutoStartWithWindows { get; set; } = false;
    }

    public class AnimationProfile
    {
        public List<string> Frames { get; set; } = [];
        public int FrameDelayMs { get; set; } = 150;
        public int LoopDelayMs { get; set; } = 0;
    }
}