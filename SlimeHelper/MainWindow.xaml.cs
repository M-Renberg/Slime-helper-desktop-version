using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // Den globala timern som ser till att pratbubblor aldrig krockar!
        private DispatcherTimer? _interactionTimer;

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
        private readonly GlobalWordWatcher _wordWatcher = new();
        private FileSystemWatcher? _cliCommandWatcher;

        private bool _isAfkSleeping = false;

        // Animation State Machine variabler
        private CancellationTokenSource? _animationCts;
        private readonly Dictionary<string, AnimationProfile> _animationProfiles = [];
        private string _currentPlayingState = "";
        private DateTime _lastRandomChatter = DateTime.MinValue;
        private bool _isAsleep = false;
        private readonly CalendarWatcher _calendarWatcher = new();
        private DispatcherTimer? _calendarTimer;

        //new
        private DispatcherTimer? _motionTimer;
        private readonly Stopwatch _motionClock = Stopwatch.StartNew();

        private double _impactVelocity = 0;
        private double _impactOffset = 0;

        private DateTime _nextIdleFlavorAt = DateTime.Now.AddSeconds(15);
        private double _flavorAngle = 0;
        private double _flavorX = 0;

        //memoney
        private readonly SlimeBrainManager _brainManager = new();

        public MainWindow()
        {
            InitializeComponent();

            DependencyPropertyDescriptor
                .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .AddValueChanged(SpeechText, (s, e) => SpeechScroll.ScrollToTop());

            LoadSettings();
            LoadAnimations();
            StartMotionLoop();
            UpdateCliMenuItem();
            var settings = LoadFullSettings();

            statusFilePath = Path.Combine(Path.GetTempPath(), "slime_status.txt");

            checkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            checkTimer.Tick += CheckStatus;
            checkTimer.Start();

            _activityWatcher.OnReaction += (status, msg) => HandleWatcherReaction(status, msg);
            _codeWatcher.OnReaction += (status, msg) => HandleWatcherReaction(status, msg);
            _wordWatcher.RegisterInstance();
            _wordWatcher.OnReaction += (status, msg) => HandleWatcherReaction(status, msg);

            _calendarTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _calendarTimer.Tick += async (s, e) => await CheckCalendarAsync();
            _calendarTimer.Start();

            // Kör en koll direkt vid start
            _ = CheckCalendarAsync();

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

            Closed += (s, e) =>
            {
                _wordWatcher.Dispose();
                _activityWatcher.Dispose();
                _codeWatcher.Dispose();
            };
        }

        // --- SMARTA TIMERS FÖR MEDDELANDEN ---
        private void StartInteractionTimer(int seconds)
        {
            _interactionTimer?.Stop();

            _interactionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _interactionTimer.Tick += (s, e) =>
            {
                isInteracting = false;
                SpeechBubble.Visibility = Visibility.Collapsed;
                ShowSlimeReaction("IDLE", "");
                RunStatusCheck();
                _interactionTimer?.Stop();
            };
            _interactionTimer.Start();
        }

        private void SpeechBubble_MouseEnter(object sender, MouseEventArgs e)
        {
            // Pausa auto-dölj medan man läser/scrollar (bara timer-styrda meddelanden)
            if (isInteracting) _interactionTimer?.Stop();
        }

        private void SpeechBubble_MouseLeave(object sender, MouseEventArgs e)
        {
            if (isInteracting && !_isAfkSleeping && SpeechBubble.Visibility == Visibility.Visible)
                StartInteractionTimer(3);
        }

        private void ShowTempMessage(string text, string emotion, int seconds = 3)
        {
            isInteracting = true;
            SpeechText.Text = text;
            SpeechText.Foreground = Brushes.Black;
            SpeechBubble.Visibility = Visibility.Visible;
            ShowSlimeReaction(emotion, "");

            StartInteractionTimer(seconds);
        }

        private void HandleWatcherReaction(string status, string msg)
        {
            if (_isAsleep) return;

            Dispatcher.Invoke(() =>
            {
                // AFK-sömn: håll SLEEP tills användaren är tillbaka (ingen timer)
                if (status == "SLEEP")
                {
                    _isAfkSleeping = true;
                    _interactionTimer?.Stop();
                    isInteracting = true; // blockerar statusfil-polling och idle-flavor medan hon sover
                    ShowSlimeReaction("SLEEP", msg);
                    HideBubbleAfterDelay(4);
                    return;
                }

                // Första reaktionen efter AFK-sömn = väckning, ska alltid gå igenom
                bool wakingUp = _isAfkSleeping;
                if (wakingUp)
                {
                    _isAfkSleeping = false;
                    isInteracting = false;
                }

                // Spärr för slumpmässigt IDLE-prat (gäller inte väckningen)
                if (status == "IDLE" && !string.IsNullOrEmpty(msg) && !wakingUp)
                {
                    if ((DateTime.Now - _lastRandomChatter).TotalMinutes < 10) return;
                    _lastRandomChatter = DateTime.Now;
                }

                if (!string.IsNullOrEmpty(msg))
                {
                    int displayTime = Math.Max(4, msg.Length / 20);
                    ShowTempMessage(msg, status, displayTime);
                }
                else
                {
                    ShowSlimeReaction(status, "");
                }
            });
        }

        // Dölj "Zzz..."-bubblan efter en stund, men behåll sömn-animationen
        private async void HideBubbleAfterDelay(int seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (_isAfkSleeping) SpeechBubble.Visibility = Visibility.Collapsed;
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
                    profile.FrameDelayMs = 200; // Perfekt andningsrytm
                    profile.LoopDelayMs = 1500;

                    if (files.Count >= 3)
                    {
                        var pingPong = new List<string>(files);
                        // Fixat till >= 0 så den garanterat slutar på rätt bildruta för en full cykel
                        for (int i = files.Count - 2; i >= 0; i--)
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
                else return;
            }

            if (_currentPlayingState == stateKey && _animationCts != null && !_animationCts.IsCancellationRequested)
            {
                return;
            }

            _currentPlayingState = stateKey;

            TriggerImpact(stateKey is "POKE" or "PUSH" or "HURRAY" or "WARNING" ? 1.6 : 1.0);

            _animationCts?.Cancel();
            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;

            var profile = _animationProfiles[stateKey];
            if (profile.Frames.Count == 0) return;

            // Bara fade på allra första bilden vid en ny status
            Dispatcher.Invoke(() => UpdateImage(profile.Frames[0], useFade: true));

            try
            {
                // Väntar in fadeIn (100) + fadeOut (100) innan loopen fortsätter
                await Task.Delay(220, token);

                while (!token.IsCancellationRequested)
                {
                    foreach (var framePath in profile.Frames)
                    {
                        token.ThrowIfCancellationRequested();

                        // Alla rutor inuti andningen/loopen byts direkt utan fade
                        Dispatcher.Invoke(() => UpdateImage(framePath, useFade: false));

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

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            if (useFade)
            {
                // Mjukare fade till 0.2
                var fadeOut = new DoubleAnimation
                {
                    To = 0.2,
                    Duration = TimeSpan.FromMilliseconds(100)
                };

                fadeOut.Completed += (s, e) =>
                {
                    SlimeImage.Source = bitmap;
                    var fadeIn = new DoubleAnimation
                    {
                        From = 0.2,
                        To = 0.6,
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
                    SlimeImage.BeginAnimation(OpacityProperty, fadeIn);
                };

                SlimeImage.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                // Direktbyte utan animering för andningsrutorna
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
            _wordWatcher.Dispose();
            _activityWatcher.Dispose();
            _codeWatcher.Dispose();
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
            ShowTempMessage(pokePhrase[index], "POKE", 4);
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
                        else if (command.StartsWith("BIND_REPO:", StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(commandFile, "");
                            var parts = command[10..].Split('|');
                            if (parts.Length == 2)
                            {
                                var (ok, msg) = _brainManager.BindRepo(parts[0], parts[1]);
                                if (ok)
                                {
                                    ShowTempMessage($"Bound {msg} to this folder! 🧠", "CUTE", 4);
                                    PlaySounds("Idle.wav");
                                }
                                else
                                {
                                    ShowTempMessage($"Bind failed: {msg}", "ERROR", 6);
                                }
                            }
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
                ShowTempMessage(greeting, "IDLE", 3);
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

        private void OpenCli_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlimeHelper", "bin");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k slime",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);

                ShowTempMessage("Spawning CLI companion! 🚀", "FUNNY", 3);
            }
            catch (Exception)
            {
                ShowTempMessage("Could not open CLI terminal.", "ERROR", 3);
            }
        }

        private void TriggerOpenNotes()
        {
            try
            {
                string commandFile = Path.Combine(Path.GetTempPath(), "slime_command.txt");
                File.WriteAllText(commandFile, "");

                var settings = LoadFullSettings();
                string targetPath = "";

                // Kolla om Obsidian-vault finns, annars öppna repos eller temp-mappen
                if (!string.IsNullOrEmpty(settings.ObsidianVaultPath) && Directory.Exists(settings.ObsidianVaultPath))
                {
                    targetPath = settings.ObsidianVaultPath;
                }
                else if (!string.IsNullOrEmpty(settings.ReposRootPath) && Directory.Exists(settings.ReposRootPath))
                {
                    targetPath = settings.ReposRootPath;
                }
                else
                {
                    targetPath = Path.GetTempPath();
                }

                // Öppna mappen i Utforskaren (eller Obsidian om det är ett vault)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });

                ShowTempMessage("Opening your notes/workspace!", "FUNNY", 3);
            }
            catch (Exception ex)
            {
                ShowTempMessage($"Oops! Couldn't open notes: {ex.Message}", "ERROR", 3);
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

            ShowTempMessage("Gemini key saved to my settings! ✨", "IDLE", 3);
            PlaySounds("Idle.wav");
        }

        private void InstallCliToPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlimeHelper", "bin");
                Directory.CreateDirectory(targetDir);

                // Vi måste kopiera alla nödvändiga filer för .NET, inte bara exe!
                string[] filesToCopy = { "slime.exe", "SlimeCli.dll", "SlimeCli.runtimeconfig.json", "SlimeCli.deps.json" };

                bool exeFound = false;

                foreach (var file in filesToCopy)
                {
                    string sourcePath = Path.Combine(baseDir, file);
                    string targetPath = Path.Combine(targetDir, file);

                    if (File.Exists(sourcePath))
                    {
                        File.Copy(sourcePath, targetPath, true);
                        if (file == "slime.exe") exeFound = true;
                    }
                }

                if (!exeFound)
                {
                    ShowTempMessage("ERROR, slime.exe was not found in the application directory.", "ERROR", 4);
                    return;
                }

                // Hämta nuvarande User PATH och lägg till mappen om den saknas
                string currentPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
                var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

                if (!paths.Any(p => string.Equals(p.Trim(), targetDir, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(targetDir);
                    string newPath = string.Join(";", paths);
                    Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.User);
                }

                ShowTempMessage("CLI installed! Restart your terminal and run 'slime help'.", "FUNNY", 4);
            }
            catch (Exception ex)
            {
                ShowTempMessage($"Failed to install CLI: {ex.Message}", "ERROR", 4);
            }
            UpdateCliMenuItem();
        }

        private void UpdateCliMenuItem()
        {
            string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlimeHelper", "bin");
            string exePath = Path.Combine(targetDir, "slime.exe");

            if (File.Exists(exePath))
            {
                CliMenuItem.Header = "Open Slime CLI";
                CliMenuItem.Click -= InstallCliToPath_Click;
                CliMenuItem.Click += OpenCli_Click;
            }
            else
            {
                CliMenuItem.Header = "Install CLI to PATH";
                CliMenuItem.Click -= OpenCli_Click;
                CliMenuItem.Click += InstallCliToPath_Click;
            }
        }

        // Skickar svaret tillbaka till CLI:t (om kommandot kom därifrån)
        private static void WriteCliResponse(string? requestId, string text, bool isError = false)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            try
            {
                string finalPath = Path.Combine(Path.GetTempPath(), $"slime_response_{requestId}.json");
                string tmpPath = finalPath + ".tmp";
                string json = JsonSerializer.Serialize(new { RequestId = requestId, Text = text, IsError = isError });
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, finalPath, true); // atomiskt, så CLI:t aldrig läser en halv fil
            }
            catch { }
        }

        private async void ProcessAiRequest(string prompt, string? cliRequestId = null, string? workingDir = null)
        {
            isInteracting = true;
            _interactionTimer?.Stop();
            string response = "";

            SpeechText.Text = "Hmm... let me think...";
            SpeechText.Foreground = Brushes.Black;
            SpeechBubble.Visibility = Visibility.Visible;
            ShowSlimeReaction("THINKING", "");

            try
            {
                var settings = LoadFullSettings();
                IAiProvider provider = GetAiProvider(settings.SelectedProvider);
                string apiKey = (settings.SelectedProvider == "Claude") ? settings.ClaudeKey : settings.GeminiKey;

                if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "Enter your Key here!")
                {
                    throw new InvalidOperationException($"Missing API Key for {settings.SelectedProvider}. Check settings!");
                }

                // 1. Vilken konversationstråd är vi i? 
                // 1. Vilken konversationstråd är vi i?
                string activeThread = "General";
                var words = prompt.Split(new[] { ' ', '\n', '\r', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries);

                // Bundna repos nämnda i prompten (med eller utan #)
                string cwdHint = "";
                var boundRepos = _brainManager.GetBoundReposFromPrompt(prompt);
                var boundPaths = boundRepos.Select(r => r.Path).ToList();
                string targetRepoPath = boundRepos.Count > 0 ? boundRepos[0].Path : "";

                if (boundRepos.Count > 0)
                {
                    activeThread = boundRepos[0].Tag;
                }
                else
                {
                    // Explicit #tagg som inte är bunden -> tydligt besked i stället för påhittade verktyg
                    var firstTag = words.FirstOrDefault(w => w.StartsWith("#"));
                    if (firstTag != null)
                    {
                        SpeechText.Text = $"Jag har inget repo bundet till {firstTag.ToLowerInvariant()}. Gå till repots mapp och kör: slime bind {firstTag.TrimStart('#')}";
                        WriteCliResponse(cliRequestId, SpeechText.Text, true);
                        ShowSlimeReaction("UNSURE", "");
                        StartInteractionTimer(8);
                        return;
                    }

                    // Ingen tagg i prompten: står CLI:t i ett bundet repo (eller en undermapp) använder vi det
                    var cwdRepo = _brainManager.FindBoundRepoForDirectory(workingDir);
                    if (cwdRepo != null)
                    {
                        boundRepos.Add((cwdRepo.Value.Tag, cwdRepo.Value.Path));
                        boundPaths.Add(cwdRepo.Value.Path);
                        targetRepoPath = cwdRepo.Value.Path;
                        activeThread = cwdRepo.Value.Tag;

                        string relCwd = Path.GetRelativePath(cwdRepo.Value.Path, workingDir!).Replace('\\', '/');
                        if (relCwd == ".")
                        {
                            cwdHint = $"[CLI Location] The user's terminal is in the ROOT of the bound workspace '{cwdRepo.Value.Tag}' ({cwdRepo.Value.Path}).";
                        }
                        else
                        {
                            cwdHint = $"[CLI Location] The user's terminal is in the subfolder '{relCwd}' of the bound workspace '{cwdRepo.Value.Tag}' ({cwdRepo.Value.Path}). " +
                                      $"Relative file names the user mentions most likely refer to that folder (try '{relCwd}/<name>' first).";
                        }
                    }
                }

                // Platsraden följer alltid med när prompten kommer från CLI:t, så hon aldrig behöver gissa var användaren är
                if (string.IsNullOrEmpty(cwdHint) && !string.IsNullOrWhiteSpace(workingDir))
                {
                    cwdHint = $"[CLI Location] The user's terminal is in '{workingDir}'.";
                }
                if (!string.IsNullOrEmpty(cwdHint))
                {
                    cwdHint += " If asked where you or the user are, or what the current folder is, answer from this line and never guess.";
                }

                string standardContext = ContextManager.BuildFullContext(settings, prompt, lastStatus);
                string tagContext = "";

                foreach (var path in boundPaths)
                {
                    tagContext += $"\n[Bound Context for {new DirectoryInfo(path).Name}]\n";
                    string? readmeFile = Directory.GetFiles(path, "README.md", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (readmeFile != null)
                    {
                        string readmeContent = File.ReadAllText(readmeFile);
                        tagContext += $"--- README.md ---\n{(readmeContent.Length > 1000 ? readmeContent.Substring(0, 1000) + "..." : readmeContent)}\n";
                    }

                    // Sök i det faktiska kod-repot
                    var searchResults = ObsidianService.SearchVaultContent(path, prompt, 2);
                    if (searchResults.Any())
                    {
                        tagContext += "--- Relevant Files Found in Repo ---\n" + string.Join("\n", searchResults) + "\n";
                    }
                }

                // Sök även i Slimes EGNA anteckningar för detta repo!
                if (activeThread != "General")
                {
                    string brainRepoPath = _brainManager.GetRepoBrainPath(activeThread);
                    if (!string.IsNullOrEmpty(brainRepoPath))
                    {
                        var brainResults = ObsidianService.SearchVaultContent(brainRepoPath, prompt, 3);
                        if (brainResults.Any())
                        {
                            tagContext += "--- Slime's Internal Notes for this Repo ---\n" + string.Join("\n", brainResults) + "\n";
                        }
                    }
                }

                string historyContext = _brainManager.GetHistoryContext(activeThread, 6);
                var queryBuilder = new System.Text.StringBuilder();

                if (!string.IsNullOrWhiteSpace(standardContext) || !string.IsNullOrWhiteSpace(tagContext))
                {
                    queryBuilder.AppendLine("[System Context]");
                    if (!string.IsNullOrWhiteSpace(standardContext)) queryBuilder.AppendLine(standardContext);
                    if (!string.IsNullOrWhiteSpace(tagContext)) queryBuilder.AppendLine(tagContext);
                }

                // NYTT: Lägg till AI-verktygen dynamiskt via Tool Managern!
                if (activeThread != "General")
                {
                    queryBuilder.AppendLine(SlimeToolManager.GetToolInstructions(targetRepoPath));
                }

                if (!string.IsNullOrWhiteSpace(historyContext))
                {
                    queryBuilder.AppendLine($"\n[Conversation History ({activeThread})]");
                    queryBuilder.AppendLine(historyContext);
                }

                if (!string.IsNullOrEmpty(cwdHint)) queryBuilder.AppendLine("\n" + cwdHint);

                // Tydliga sökfrågor ("filer med ordet X i sig", "var används X") söker vi åt henne direkt.
                // Då behöver hon inte välja rätt verktyg själv, och svaret bygger på riktiga träffar.
                string? preSearchTerm = !string.IsNullOrEmpty(targetRepoPath) ? SlimeToolManager.ExtractSearchTerm(prompt) : null;
                if (preSearchTerm != null)
                {
                    string preResults = SlimeToolManager.RunSearch(targetRepoPath, preSearchTerm);
                    queryBuilder.AppendLine($"\n[Pre-fetched Search Results for \"{preSearchTerm}\"]");
                    queryBuilder.AppendLine("```");
                    queryBuilder.AppendLine(preResults);
                    queryBuilder.AppendLine("```");
                    queryBuilder.AppendLine("These results were already fetched for the user's request. Answer directly from them: show the actual matching files and lines, using the exact paths shown. " +
                                            "If a section ends with '... (+N more)', tell the user that N more exist that you were not shown. Never add entries that are not in the results. " +
                                            "Do NOT use LIST_FILES or SEARCH_FILES for this again.");
                }

                queryBuilder.AppendLine("\n[User Prompt]");
                queryBuilder.AppendLine(prompt);

                string fullQuery = queryBuilder.ToString();

                // Skicka till AI
                // 1. Första AI-anropet
                response = await AiService.AskSlime(fullQuery, provider, apiKey);
                response = SlimeToolManager.StripHallucinatedTurns(response);

                // 2. Kolla om hon använde verktyg (t.ex. [READ_FILE])
                string followUpPrompt;
                string cleanResponse = SlimeToolManager.ProcessResponse(response, activeThread, targetRepoPath, _brainManager, out followUpPrompt);

                // Spara första steget i historiken
                _brainManager.SaveMessage(activeThread, "User", prompt);
                _brainManager.SaveMessage(activeThread, "Slime", response);

                // 3. AGENT-LOOP: Om hon bad om att få läsa en fil, skicka tillbaka filinnehållet till henne i smyg!
                var agentLog = new System.Text.StringBuilder();
                int rounds = 0;
                const int maxRounds = 4;

                while (!string.IsNullOrEmpty(followUpPrompt) && rounds++ < maxRounds)
                {
                    SpeechText.Text = cleanResponse; // UI:t visar t.ex. "Jag läser filen..."

                    // Spara bara en kort markör i historiken, annars hamnar hela filinnehåll i chat_history.json
                    string shortNote = followUpPrompt.Length > 400
                        ? followUpPrompt.Substring(0, 400) + "... [truncated]"
                        : followUpPrompt;
                    _brainManager.SaveMessage(activeThread, "System", shortNote);

                    // Hela uppföljningarna från den här frågan behålls i loopen så att hon minns tidigare rundor
                    agentLog.AppendLine(followUpPrompt);
                    agentLog.AppendLine();

                    // Uppföljningen får verktygsinstruktionerna, den ursprungliga frågan och tydlig order att svara nu.
                    // Annars "kollar" hon bara och visar aldrig resultatet för användaren.
                    var loopBuilder = new System.Text.StringBuilder();
                    if (!string.IsNullOrEmpty(cwdHint)) loopBuilder.AppendLine(cwdHint + "\n");
                    if (activeThread != "General") loopBuilder.AppendLine(SlimeToolManager.GetToolInstructions(targetRepoPath));

                    loopBuilder.AppendLine($"\n[Conversation History ({activeThread})]");
                    loopBuilder.AppendLine(_brainManager.GetHistoryContext(activeThread, 6));

                    loopBuilder.AppendLine("\n[Original User Request]");
                    loopBuilder.AppendLine(prompt);

                    loopBuilder.AppendLine("\n[Tool Results]");
                    loopBuilder.AppendLine(agentLog.ToString());

                    loopBuilder.AppendLine("[Instructions]");
                    loopBuilder.AppendLine("The tool results above are the data you asked for. Answer the original user request NOW, based on them. " +
                                           "Show the user the actual information (for example the file names or the file content) in your reply - do not just say that you looked. " +
                                           "Do not call a tool again for something already in the results above. Only use another tool if you still need information that is not there.");

                    string loopQuery = loopBuilder.ToString();

                    response = await AiService.AskSlime(loopQuery, provider, apiKey);
                    response = SlimeToolManager.StripHallucinatedTurns(response);

                    // Processa nya verktyg i svaret; en ny uppföljning startar nästa runda
                    cleanResponse = SlimeToolManager.ProcessResponse(response, activeThread, targetRepoPath, _brainManager, out followUpPrompt);

                    _brainManager.SaveMessage(activeThread, "Slime", response);
                }

                // 4. Visa det slutgiltiga resultatet i appen
                SpeechText.Text = cleanResponse;
                WriteCliResponse(cliRequestId, cleanResponse);
                ShowSlimeReaction("IDLE", "");
                PlaySounds("Idle.wav");
            }
            catch (Exception ex)
            {
                SpeechText.Text = $"Brain freeze! {ex.Message}";
                WriteCliResponse(cliRequestId, SpeechText.Text, true);
                ShowSlimeReaction("ERROR", "");
                SpeechText.Foreground = Brushes.Red;
            }

            int displayTime = Math.Max(6, SpeechText.Text.Length / 20);
            StartInteractionTimer(displayTime);
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

                ShowTempMessage($"Brain switched to {selectedProvider}!", "CUTE", 3);
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

            ShowTempMessage("Claude is ready to think!", "HURRAY", 3);
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

                // NYTT: Tvinga SlimeBrain att flytta och bygga upp sig i Obsidian-valvet!
                _brainManager.RelocateToObsidian(dialog.FolderName);

                ShowTempMessage("Obsidian Vault connected & Brain Created!", "NOTES", 3);
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

                _ = PlayAnimationLoop(status);
            });
        }

        private async Task CheckCalendarAsync()
        {
            try
            {
                string? nextEvent = await _calendarWatcher.GetNextEventAsync();
                if (!string.IsNullOrEmpty(nextEvent))
                {
                    string message = $"Upcoming: {nextEvent}";
                    ShowTempMessage(message, "NOTES", 5);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not fetch calender: {ex.Message}");
            }
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

                ShowTempMessage("Git Repositories folder linked!", "NOTES", 3);
                PlaySounds("Idle.wav");
            }
        }

        private void OnOpenBrowserClick(object sender, RoutedEventArgs e)
        {
            string url = SlimeInputDialog.Show("Open Browser", "Enter URL or search query:", "https://github.com");

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    ShowTempMessage("Opening browser!", "CUTE", 3);
                    PlaySounds("Idle.wav");
                }
                catch (Exception ex)
                {
                    ShowTempMessage($"Could not open browser: {ex.Message}", "ERROR", 5);
                }
            }
        }

        //Notebook:

        private async Task HandleAnalyzeDatasetCommand(string csvPath)
        {
            try
            {
                if (!File.Exists(csvPath))
                {
                    ShowTempMessage("Dataset file not found!", "ERROR", 5);
                    return;
                }

                var settings = LoadFullSettings();
                IAiProvider provider = GetAiProvider(settings.SelectedProvider);
                string apiKey = (settings.SelectedProvider == "Claude") ? settings.ClaudeKey : settings.GeminiKey;
                if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "Enter your Key here!")
                {
                    throw new InvalidOperationException($"Missing API Key for {settings.SelectedProvider}. Check settings!");
                }

                // 1. Läs in de första 15 raderna av CSV-filen
                var csvLines = File.ReadLines(csvPath).Take(15).ToList();
                string csvSample = string.Join(Environment.NewLine, csvLines);
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(csvPath);
                string fileName = Path.GetFileName(csvPath);

                // 2. Bygg prompten säkert utan klammer-krockar
                string prompt = "You are an expert Data Scientist. Analyze the following CSV columns and sample data for the file '" + fileName + "':\n" +
                                "```csv\n" + csvSample + "\n```\n" +
                                "Create a complete Exploratory Data Analysis (EDA) Jupyter Notebook in JSON format.\n" +
                                "The response MUST be a valid JSON object matching this schema exactly, without extra markdown formatting around it (pure JSON only):\n" +
                                "{\n" +
                                "  \"cells\": [\n" +
                                "    {\n" +
                                "      \"cell_type\": \"markdown\",\n" +
                                "      \"metadata\": {},\n" +
                                "      \"source\": [\"# Title\\n\", \"Description...\"]\n" +
                                "    },\n" +
                                "    {\n" +
                                "      \"cell_type\": \"code\",\n" +
                                "      \"metadata\": {},\n" +
                                "      \"source\": [\"import pandas as pd\\n\", \"df = pd.read_csv('...')\"],\n" +
                                "      \"outputs\": []\n" +
                                "    }\n" +
                                "  ]\n" +
                                "}\n" +
                                "Include cells for:\n" +
                                "1. Markdown with introduction.\n" +
                                "2. Code to read the CSV and display df.head(), df.info(), and df.describe().\n" +
                                "3. Code for basic visualizations using Seaborn/Matplotlib.\n" +
                                "Return ONLY valid JSON.";

                ShowTempMessage("Slime is analyzing dataset... 📊", "WORKING", 4);
                PlaySounds("Idle.wav");

                // 3. Anropa AI-tjänsten (byt ut _aiService mot ditt faktiska fältnamn om det skiljer sig)
                string aiResponse = await AiService.AskSlime(prompt, provider, apiKey);
                // Rensa bort eventuella kodblock
                aiResponse = aiResponse.Trim();


                if (aiResponse.StartsWith("```json")) aiResponse = aiResponse[7..];
                if (aiResponse.StartsWith("```")) aiResponse = aiResponse[3..];
                if (aiResponse.EndsWith("```")) aiResponse = aiResponse[..^3];
                aiResponse = aiResponse.Trim();

                // 4. Deserialisera till NotebookRoot
                var notebook = System.Text.Json.JsonSerializer.Deserialize<NotebookRoot>(aiResponse);
                if (notebook == null || notebook.Cells.Count == 0)
                {
                    throw new Exception("Could not parse AI response into a valid notebook.");
                }

                // 5. Hitta var filen ska sparas
                string targetDirectory = Path.GetDirectoryName(csvPath) ?? Directory.GetCurrentDirectory();

                string notebooksDir = Path.Combine(targetDirectory, "notebooks");
                Directory.CreateDirectory(notebooksDir);

                string outputPath = Path.Combine(notebooksDir, $"{fileNameWithoutExt}_analysis.ipynb");

                // 6. Spara ner .ipynb-filen
                string jsonOutput = System.Text.Json.JsonSerializer.Serialize(notebook, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outputPath, jsonOutput);

                ShowTempMessage("Notebook created!", "NOTES", 4);
                PlaySounds("Idle.wav");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error analyzing dataset: {ex.Message}");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "slime_analyze_error.txt"), ex.ToString());
                ShowTempMessage($"Failed to analyze: {ex.Message}", "ERROR", 6);
            }
        }


        private void SleepMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _isAsleep = !_isAsleep;

            if (_isAsleep)
            {
                SleepMenuItem.Header = "Wake Up";
                ShowSlimeReaction("SLEEP", "Zzz... Goodnight...");
            }
            else
            {
                SleepMenuItem.Header = "Put to Sleep";
                ShowSlimeReaction("IDLE", "I'm awake again!");
            }
        }


        //animation

        private void StartMotionLoop()
        {
            _motionTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60 fps
            };
            _motionTimer.Tick += (s, e) => UpdateMotion();
            _motionTimer.Start();
        }

        private void UpdateMotion()
        {
            double t = _motionClock.Elapsed.TotalSeconds;

            // Kontinuerlig andning – körs alltid, oavsett vilken bild som visas
            double breathScale = 1.0 + Math.Sin(t * 1.6) * 0.015;
            double breathY = Math.Sin(t * 1.6) * 1.5;

            // Impact-puls: dämpad fjäder som studsar tillbaka mot 0
            const double stiffness = 220.0, damping = 18.0, dt = 0.016;
            double force = -stiffness * _impactOffset - damping * _impactVelocity;
            _impactVelocity += force * dt;
            _impactOffset += _impactVelocity * dt;

            // Sällsynt idle-flavor (bara i IDLE, ingen ny konst behövs)
            if (DateTime.Now >= _nextIdleFlavorAt && !isInteracting && _currentPlayingState == "IDLE")
            {
                _ = PlayIdleFlavor();
                _nextIdleFlavorAt = DateTime.Now.AddSeconds(rng.Next(20, 45));
            }

            SlimeScale.ScaleX = breathScale - _impactOffset * 0.4;
            SlimeScale.ScaleY = breathScale + _impactOffset * 0.6;
            SlimeTranslate.Y = breathY;
            SlimeTranslate.X = _flavorX;
            SlimeRotate.Angle = _flavorAngle;
        }

        // Anropas när en ny state faktiskt börjar spelas
        private void TriggerImpact(double strength = 1.0)
        {
            _impactVelocity -= 6.0 * strength;
        }

        private async Task PlayIdleFlavor()
        {
            int variant = rng.Next(0, 2); // 0 = titta åt sidan, 1 = liten lutning tillbaka
            double angle = variant == 0 ? (rng.Next(0, 2) == 0 ? -2 : 2) : 0;
            double x = variant == 0 ? angle * 1.5 : 0;

            await AnimateFlavorTo(angle, x, 400);
            await Task.Delay(900);
            await AnimateFlavorTo(0, 0, 500);
        }

        private async Task AnimateFlavorTo(double angle, double x, int durationMs)
        {
            double startAngle = _flavorAngle, startX = _flavorX;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < durationMs)
            {
                double p = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
                double eased = 1 - Math.Pow(1 - p, 3);
                _flavorAngle = startAngle + (angle - startAngle) * eased;
                _flavorX = startX + (x - startX) * eased;
                await Task.Delay(16);
            }
            _flavorAngle = angle;
            _flavorX = x;
        }


        //LOAD AND SAVE FUNCTIONS
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


        //CLI
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
                    string fileContent = File.ReadAllText(commandFile).Trim();

                    if (!string.IsNullOrWhiteSpace(fileContent))
                    {
                        string prompt = "";
                        string? requestId = null;
                        string? workingDir = null;

                        // Säker parsning: Kolla om det är JSON först
                        if (fileContent.StartsWith("{"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(fileContent);
                                if (doc.RootElement.TryGetProperty("Prompt", out var promptProp))
                                {
                                    prompt = promptProp.GetString() ?? "";
                                }
                                if (doc.RootElement.TryGetProperty("RequestId", out var idProp))
                                {
                                    requestId = idProp.GetString();
                                }
                                if (doc.RootElement.TryGetProperty("WorkingDir", out var wdProp))
                                {
                                    workingDir = wdProp.GetString();
                                }
                            }
                            catch { /* Svälj trasig JSON och gå vidare, faller tillbaka på tom sträng */ }
                        }
                        else
                        {
                            // Om det inte är formaterad JSON, anta att det är råtext från CLI
                            prompt = fileContent;
                        }

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
                                StartMotionLoop();
                                ShowTempMessage($"Changed skin to {currentSkin}!", "FUNNY", 3);
                            });
                        }
                        else if (prompt.StartsWith("CAL_ADD:", StringComparison.OrdinalIgnoreCase))
                        {
                            string eventTitle = prompt[8..].Trim();

                            _ = Task.Run(async () =>
                            {
                                bool success = await _calendarWatcher.AddEventAsync(eventTitle);

                                Dispatcher.Invoke(() =>
                                {
                                    if (success)
                                    {
                                        ShowTempMessage($"Added \"{eventTitle}\" to calendar!", "NOTES", 5);
                                        PlaySounds("Idle.wav");
                                    }
                                    else
                                    {
                                        ShowTempMessage("Failed to add to calendar...", "ERROR", 5);
                                    }
                                });
                            });
                        }
                        else if (prompt.StartsWith("ANALYZE_DATASET:", StringComparison.OrdinalIgnoreCase))
                        {
                            string csvPath = prompt[16..].Trim();
                            _ = Task.Run(() => HandleAnalyzeDatasetCommand(csvPath));
                        }
                        else if (prompt.StartsWith("BIND_REPO:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = prompt[10..].Split('|');
                            if (parts.Length == 2)
                            {
                                string tag = parts[0];
                                string path = parts[1];

                                Dispatcher.Invoke(() =>
                                {
                                    var (ok, msg) = _brainManager.BindRepo(tag, path);
                                    if (ok)
                                    {
                                        ShowTempMessage($"Bound {msg} to this folder! 🧠", "CUTE", 4);
                                        PlaySounds("Idle.wav");
                                        WriteCliResponse(requestId, $"Bound {msg} → {path}");
                                    }
                                    else
                                    {
                                        ShowTempMessage($"Bind failed: {msg}", "ERROR", 6);
                                        WriteCliResponse(requestId, $"Bind failed: {msg}", true);
                                    }
                                });
                            }
                            else
                            {
                                WriteCliResponse(requestId, "Bind failed: ogiltigt kommando.", true);
                            }
                        }
                        else if (!string.IsNullOrEmpty(prompt))
                        {
                            Dispatcher.Invoke(() => ProcessAiRequest(prompt, requestId, workingDir));
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