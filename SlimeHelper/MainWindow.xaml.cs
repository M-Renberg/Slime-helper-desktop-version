using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.RegularExpressions;


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
        private bool _isAiBusy = false;

        // Animation State Machine variabler
        private CancellationTokenSource? _animationCts;
        private readonly Dictionary<string, AnimationProfile> _animationProfiles = [];
        private string _currentPlayingState = "";
        private DateTime _lastRandomChatter = DateTime.MinValue;
        private bool _isAsleep = false;
        private readonly CalendarWatcher _calendarWatcher = new();
        private DispatcherTimer? _calendarTimer;

        // GitHub Watcher
        private readonly GitHubWatcher _gitHubWatcher = new();
        private GitHubWorkflowRun? _lastFailedRun;
        private string? _lastFailedLogSnippet;

        public GitHubWatcher GitHubWatcher => _gitHubWatcher;

        //new
        private DispatcherTimer? _motionTimer;
        private readonly Stopwatch _motionClock = Stopwatch.StartNew();

        private double _impactVelocity = 0;
        private double _impactOffset = 0;

        private DateTime _nextIdleFlavorAt = DateTime.Now.AddSeconds(15);
        private double _flavorAngle = 0;
        private double _flavorX = 0;

        // Motion 2.0: Skew inertia, talking bounce, jump/drop physics & autonomous roaming
        private double _skewOffset = 0;
        private double _skewVelocity = 0;
        private double _jumpOffsetY = 0;
        private double _jumpVelocityY = 0;
        private double _lastDragVelX = 0;
        private double _lastDragY = 0;
        private DateTime _talkingUntil = DateTime.MinValue;
        private bool _allowDesktopRoaming = true;
        private bool _isRoamingHopActive = false;
        private DateTime _nextRoamingHopAt = DateTime.Now.AddSeconds(40);

        // Phase 1: Poke-Combo, Window Shake & Emote Particle state
        private int _pokeStreak = 0;
        private DateTime _lastPokeAt = DateTime.MinValue;
        private bool _isDraggingWindow = false;
        private double _lastDragX = 0;
        private int _lastDragDir = 0;
        private int _shakeReversals = 0;
        private DateTime _firstShakeReversalAt = DateTime.MinValue;
        private DateTime _lastShakeTriggeredAt = DateTime.MinValue;
        private DateTime _nextSleepZzzAt = DateTime.MinValue;
        private readonly List<EmoteParticle> _activeParticles = [];

        private sealed class EmoteParticle
        {
            public required TextBlock Element { get; init; }
            public required TranslateTransform Translate { get; init; }
            public required RotateTransform Rotate { get; init; }
            public required ScaleTransform Scale { get; init; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Vx { get; set; }
            public double Vy { get; set; }
            public double SpinSpeed { get; set; }
            public double WavePhase { get; init; }
            public double WaveAmplitude { get; init; }
            public double Age { get; set; }
            public double Lifetime { get; init; }
        }

        //memoney
        private readonly SlimeBrainManager _brainManager = new();

        // Chat Window & Events
        private ChatWindow? _chatWindow;
        public event Action<string>? AiStatusChanged;
        public event Action<string, string>? AiResponseReceived;
        public event Action<string>? AiErrorOccurred;
        public event Action<string, string, string>? AiUserMessageReceived;
        public event Action<string>? FocusTimerCompleted;
        public event Action<SlimeMemory>? MoodChanged;

        // Phase 3: Focus / Pomodoro Timer state
        private DispatcherTimer? _focusTimer;
        private DateTime _focusEndTime = DateTime.MinValue;
        private string? _focusTaskName = null;

        // Phase 5: Mood & Energy state
        private double _currentBreathingMultiplier = 1.0;
        private int _currentMoodValue = 80;

        // Paper-Doll v1.0 runtime state
        private PaperDollProfile? _activePaperDoll = null;
        private double _eyeOffsetX = 0;
        private double _eyeOffsetY = 0;
        private bool _isPaperDollBlinking = false;
        private int _lastMouthMode = -1; // 0 = closed, 1 = open, 2 = smile
        private int _lastOverlayFrameIdx = -1;
        private readonly int[] _lastMotionFrameIdx = [-1, -1, -1];

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        private static readonly HashSet<string> IgnoredHashDirectives = new(StringComparer.OrdinalIgnoreCase)
        {
            "#if", "#else", "#elif", "#endif", "#define", "#undef",
            "#region", "#endregion", "#error", "#warning", "#line", "#pragma", "#nullable"
        };

        private static bool IsValidRepoHashTag(string w, string prompt)
        {
            if (string.IsNullOrWhiteSpace(w) || !w.StartsWith("#"))
                return false;

            // Pure '#' or markdown headers like '#', '##', '###'
            if (w.Length <= 1 || w.All(c => c == '#'))
                return false;

            // Known C# directives (case-insensitive)
            string cleanDirective = w.TrimEnd(';', ':', ')', '}', ']', '>', '"', '\'', '`');
            if (IgnoredHashDirectives.Contains(w) || IgnoredHashDirectives.Contains(cleanDirective))
                return false;

            // Digits / issue numbers like #1, #2, #42
            string tagBody = w.TrimStart('#');
            if (string.IsNullOrEmpty(tagBody) || tagBody.All(char.IsDigit) || char.IsDigit(tagBody[0]))
                return false;

            // Code punctuation inside or at the end of the hashtag
            // Valid repo tags consist only of letters, digits, underscores, or hyphens
            if (tagBody.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                return false;

            // Check if preceded or followed by code punctuation in the prompt
            int searchStart = 0;
            bool foundCleanOccurrence = false;
            while (searchStart < prompt.Length)
            {
                int idx = prompt.IndexOf(w, searchStart, StringComparison.Ordinal);
                if (idx < 0) break;

                bool precededByCode = false;
                int prevIdx = idx - 1;
                while (prevIdx >= 0 && (prompt[prevIdx] == ' ' || prompt[prevIdx] == '\t'))
                    prevIdx--;

                if (prevIdx >= 0 && "()[]{}<>;:=+*/\\\"'`~^&|%$#".Contains(prompt[prevIdx]))
                    precededByCode = true;

                bool followedByCode = false;
                int nextIdx = idx + w.Length;
                while (nextIdx < prompt.Length && (prompt[nextIdx] == ' ' || prompt[nextIdx] == '\t'))
                    nextIdx++;

                if (nextIdx < prompt.Length && "()[]{}<>;:=+*/\\\"'`~^&|%$".Contains(prompt[nextIdx]))
                    followedByCode = true;

                if (!precededByCode && !followedByCode)
                {
                    foundCleanOccurrence = true;
                    break;
                }

                searchStart = idx + w.Length;
            }

            return foundCleanOccurrence;
        }

        public MainWindow()
        {
            InitializeComponent();

            // Bekräftelsedialog innan Slime får köra bygge/test/lint i ett repo (visas på UI-tråden)
            SlimeToolManager.ConfirmRun = (title, message) =>
                System.Windows.MessageBox.Show(this, message, title,
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

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

            _activityWatcher.CustomIdleChatterProvider = (now, random) =>
            {
                try
                {
                    var currentSettings = LoadFullSettings();
                    string activeThread = _chatWindow?.CurrentThread ?? "General";
                    string[] scanFolders = _brainManager.GetTodoScanFolders(activeThread, currentSettings.ObsidianVaultPath);
                    var openTodos = NotesManager.ListUnfinishedTodos(scanFolders).Select(t => t.Text).ToList();
                    var memory = _brainManager.LoadMemory();
                    var boundTags = _brainManager.GetBoundRepos().Keys.ToList();
                    var (isFocusActive, focusRemaining, focusTask) = GetFocusStatus();
                    int remMins = isFocusActive ? Math.Max(1, (int)Math.Ceiling(focusRemaining.TotalMinutes)) : 0;

                    return SlimeResponses.PickContextualIdleChatter(
                        now,
                        random,
                        openTodos,
                        memory.Nodes,
                        boundTags,
                        (isFocusActive, remMins, focusTask),
                        (memory.Mood, memory.Energy));
                }
                catch
                {
                    return SlimeResponses.PickIdleChatter(now, random);
                }
            };
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

            // GitHub Watcher Initialization
            _gitHubWatcher.GitHubToken = settings.GitHubToken;

            _gitHubWatcher.OnWorkflowFailed += (run, logSnippet) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _lastFailedRun = run;
                    _lastFailedLogSnippet = logSnippet;
                    RecordMoodEvent("CI_FAILURE");
                    PlaySounds("Warning.wav");
                    string failureMsg = $"CI failed on {run.Name} ({run.HeadBranch})!\nClick to inspect failure.";
                    ShowTempMessage(failureMsg, "ERROR", 8);
                });
            };

            _gitHubWatcher.OnWorkflowSucceeded += (run) =>
            {
                Dispatcher.Invoke(() =>
                {
                    RecordMoodEvent("CI_SUCCESS");
                    PlaySounds("Idle.wav");
                    string successMsg = $"CI passed on {run.Name} ({run.HeadBranch})!";
                    ShowTempMessage(successMsg, "HURRAY", 6);
                });
            };

            string initialGhPath = !string.IsNullOrEmpty(settings.ReposRootPath) && Directory.Exists(settings.ReposRootPath)
                ? settings.ReposRootPath
                : Directory.GetCurrentDirectory();
            _activityWatcher.ActiveRepoPath = initialGhPath;
            _gitHubWatcher.Start(initialGhPath, settings.GitHubToken);

            // Drag function, Drag Inertia & Window Shake detection
            LocationChanged += (s, e) =>
            {
                if (!_isDraggingWindow) return;
                double velDx = Left - _lastDragVelX;
                _lastDragVelX = Left;
                double dy = Top - _lastDragY;
                _lastDragY = Top;

                // Apply gentle pendulum skew & stretch inertia based on per-step drag delta
                _skewVelocity += Math.Clamp(-velDx * 1.4, -38.0, 38.0);
                _impactVelocity += Math.Clamp(dy * 0.25, -7.0, 7.0);

                double dx = Left - _lastDragX;
                if (Math.Abs(dx) < 8) return;

                int dir = dx > 0 ? 1 : -1;
                var now = DateTime.Now;
                if (_lastDragDir != 0 && dir != _lastDragDir)
                {
                    if ((now - _firstShakeReversalAt).TotalMilliseconds > 700)
                    {
                        _shakeReversals = 1;
                        _firstShakeReversalAt = now;
                    }
                    else
                    {
                        _shakeReversals++;
                    }
                }
                _lastDragDir = dir;
                _lastDragX = Left;
            };

            MouseLeftButtonDown += (s, e) =>
            {
                startWindowPos = new Point(Left, Top);
                _isDraggingWindow = true;
                _lastDragX = Left;
                _lastDragVelX = Left;
                _lastDragY = Top;
                _lastDragDir = 0;
                _shakeReversals = 0;
                _firstShakeReversalAt = DateTime.Now;
                try
                {
                    DragMove();
                }
                finally
                {
                    _isDraggingWindow = false;
                }
            };

            MouseLeftButtonUp += (s, e) =>
            {
                double distanceMoved = Math.Abs(Left - startWindowPos.X)
                                     + Math.Abs(Top - startWindowPos.Y);

                if (_shakeReversals >= 4 && (DateTime.Now - _lastShakeTriggeredAt).TotalSeconds > 2.5)
                {
                    _lastShakeTriggeredAt = DateTime.Now;
                    _shakeReversals = 0;
                    TriggerWindowShake();
                }
                else if (distanceMoved < 5)
                {
                    PokeSlime();
                }
                else
                {
                    // Gravity drop & landing squash when releasing Slime after dragging
                    _jumpOffsetY = -16.0;
                    _jumpVelocityY = 35.0;
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

            // Double click opens Chat Window
            MouseDoubleClick += (s, e) =>
            {
                OpenChatWindow();
            };

            SetupCommandWatcher();
            ShowSlimeReaction("IDLE", "");
            RunStatusCheck();

            Closed += (s, e) =>
            {
                _gitHubWatcher.Dispose();
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

        private void TriggerTalkingForText(string? text, string? emotion)
        {
            if (!string.IsNullOrWhiteSpace(text) && !string.Equals(emotion, "SLEEP", StringComparison.OrdinalIgnoreCase))
            {
                _talkingUntil = DateTime.Now.AddMilliseconds(Math.Clamp(text.Length * 32, 650, 2100));
            }
        }

        private void ShowTempMessage(string text, string emotion, int seconds = 3)
        {
            isInteracting = true;
            SpeechText.Text = text;
            SpeechText.Foreground = Brushes.Black;
            SpeechBubble.Visibility = Visibility.Visible;
            TriggerTalkingForText(text, emotion);
            ShowSlimeReaction(emotion, "");

            StartInteractionTimer(seconds);
        }

        private void HandleWatcherReaction(string status, string msg)
        {
            if (_isAsleep) return;

            Dispatcher.Invoke(() =>
            {
                bool isIdleChatter = status.StartsWith("IDLE_CHAT:", StringComparison.OrdinalIgnoreCase);
                if (isIdleChatter)
                {
                    status = status["IDLE_CHAT:".Length..];
                }

                // AFK sleep: hold SLEEP state until the user returns (no auto-hide timer)
                if (status == "SLEEP")
                {
                    _isAfkSleeping = true;
                    _interactionTimer?.Stop();
                    isInteracting = true; // blocks status-file polling and idle flavor while asleep
                    RecordMoodEvent("SLEEP");
                    ShowSlimeReaction("SLEEP", msg);
                    HideBubbleAfterDelay(4);
                    return;
                }

                // First reaction after AFK sleep wakes Slime up and always goes through
                bool wakingUp = _isAfkSleeping;
                if (wakingUp)
                {
                    _isAfkSleeping = false;
                    isInteracting = false;
                }

                // Cooldown for random idle chatter across all idle emotions (does not block wake-up)
                if ((isIdleChatter || status == "IDLE") && !string.IsNullOrEmpty(msg) && !wakingUp)
                {
                    if ((DateTime.Now - _lastRandomChatter).TotalMinutes < 10) return;
                    _lastRandomChatter = DateTime.Now;
                }

                if (!isIdleChatter)
                {
                    if (status == "STREAK") RecordMoodEvent("STREAK");
                    else if (status == "BREAK") RecordMoodEvent("BREAK");
                    else if (status == "ANNOYED") RecordMoodEvent("SWEAR");
                    else if (status == "TIRED") RecordMoodEvent("HIGH_CPU");
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
            _animationCts?.Cancel();
            _animationCts?.Dispose();
            _animationCts = null;
            _currentPlayingState = "";
            _animationProfiles.Clear();
            string basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images", currentSkin);

            if (!Directory.Exists(basePath)) return;

            foreach (var stateDir in Directory.GetDirectories(basePath))
            {
                string stateName = new DirectoryInfo(stateDir).Name.ToUpperInvariant();

                var files = PaperDollResolver.OrderPathsNaturally(Directory.GetFiles(stateDir, "*.png"));
                if (files.Count == 0) continue;

                var profile = new AnimationProfile
                {
                    Frames = files,
                    PaperDoll = LoadPaperDollLayers(stateDir)
                };

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
            _animationCts?.Dispose();
            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;

            var profile = _animationProfiles[stateKey];
            if (profile.PaperDoll != null)
            {
                Dispatcher.Invoke(() => ActivatePaperDoll(profile.PaperDoll));
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(rng.Next(2800, 5600), token);
                        if (profile.PaperDoll.EyesBlink != null && stateKey != "SLEEP" && !_isAsleep && !_isAfkSleeping && (!isInteracting || stateKey == "THINKING"))
                        {
                            Dispatcher.Invoke(() => SetPaperDollBlink(true));
                            await Task.Delay(140, token);
                            Dispatcher.Invoke(() => SetPaperDollBlink(false));
                        }
                    }
                }
                catch (TaskCanceledException) { }
                return;
            }

            Dispatcher.Invoke(() => DeactivatePaperDoll());
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

            if (useFade && SlimeImage.Source != null && SlimeImageBack != null)
            {
                // Seamless double-buffered crossfade: keep previous frame on SlimeImageBack so total opacity never dips
                SlimeImageBack.Source = SlimeImage.Source;
                SlimeImageBack.BeginAnimation(OpacityProperty, null);
                SlimeImageBack.Opacity = 0.6;

                SlimeImage.Source = bitmap;
                var fadeIn = new DoubleAnimation
                {
                    From = 0.0,
                    To = 0.6,
                    Duration = TimeSpan.FromMilliseconds(150)
                };
                var fadeOutBack = new DoubleAnimation
                {
                    From = 0.6,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(150)
                };

                SlimeImage.BeginAnimation(OpacityProperty, fadeIn);
                SlimeImageBack.BeginAnimation(OpacityProperty, fadeOutBack);
            }
            else
            {
                // Direct frame swap for breathing/blink loops
                if (SlimeImageBack != null)
                {
                    SlimeImageBack.BeginAnimation(OpacityProperty, null);
                    SlimeImageBack.Opacity = 0.0;
                }
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
            _gitHubWatcher.Dispose();
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
            if (_isAsleep || _isAfkSleeping || _isAiBusy) return;

            var now = DateTime.Now;
            if ((now - _lastPokeAt).TotalSeconds <= 4.0)
            {
                _pokeStreak++;
            }
            else
            {
                _pokeStreak = 1;
            }
            _lastPokeAt = now;

            var (emotion, phrase, impact, emote) = SlimeResponses.GetPokeComboReaction(_pokeStreak, currentSkin, rng);
            RecordMoodEvent(_pokeStreak <= 2 ? "POKE_GENTLE" : "POKE_SPAM");

            PlaySounds(emotion == "SCARED" ? "Warning.wav" : "Poke.wav");
            TriggerImpact(impact);

            int particleCount = _pokeStreak >= 5 ? 4 : (_pokeStreak >= 3 ? 3 : 2);
            SpawnEmoteBurst(emote, particleCount, spin: _pokeStreak >= 5);

            ShowTempMessage(phrase, emotion, 4);
        }

        private void TriggerWindowShake()
        {
            if (_isAsleep || _isAfkSleeping || _isAiBusy) return;

            RecordMoodEvent("POKE_SPAM");
            PlaySounds("Warning.wav");
            TriggerImpact(2.8);
            SpawnEmoteBurst("💫", 5, spin: true);
            string msg = SlimeResponses.PickRandom(SlimeResponses.WindowShakeResponses);
            ShowTempMessage(msg, "SCARED", 4);
        }

        public void RecordMoodEvent(string eventType)
        {
            try
            {
                var memory = _brainManager.LoadMemory();
                memory.ApplyEvent(eventType);
                _brainManager.SaveMemory(memory);
                _currentBreathingMultiplier = memory.GetBreathingSpeedMultiplier();
                _currentMoodValue = memory.Mood;
                MoodChanged?.Invoke(memory);
            }
            catch { }
        }

        public SlimeMemory GetCurrentMoodMemory()
        {
            var memory = _brainManager.LoadMemory();
            _currentBreathingMultiplier = memory.GetBreathingSpeedMultiplier();
            _currentMoodValue = memory.Mood;
            return memory;
        }

        public void NotifyTodoAdded(string taskText)
        {
            PlaySounds("Idle.wav");
            TriggerImpact(1.2);
            ShowTempMessage($"Added task: {taskText}", "NOTES", 4);
        }

        public void CelebrateTodoCompleted(string taskText)
        {
            RecordMoodEvent("TODO_DONE");
            PlaySounds("Idle.wav");
            TriggerImpact(2.0);
            ShowTempMessage($"Task completed: {taskText}! Great work!", "HURRAY", 5);
        }

        public (bool Started, string Message) StartFocusTimer(int minutes, string? taskName = null)
        {
            if (minutes <= 0 || minutes > 240)
            {
                return (false, "Focus duration must be between 1 and 240 minutes.");
            }

            _focusTimer?.Stop();
            _focusEndTime = DateTime.Now.AddMinutes(minutes);
            _focusTaskName = string.IsNullOrWhiteSpace(taskName) ? null : taskName.Trim();

            _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _focusTimer.Tick += (s, e) =>
            {
                if (DateTime.Now >= _focusEndTime)
                {
                    _focusTimer.Stop();
                    string finishedTask = _focusTaskName != null ? $" on \"{_focusTaskName}\"" : "";
                    _focusTaskName = null;
                    _focusEndTime = DateTime.MinValue;

                    PlaySounds("Poke.wav");
                    TriggerImpact(2.0);
                    string doneMsg = $"Focus session{finishedTask} complete! Time to stretch and grab some water.";
                    ShowTempMessage(doneMsg, "HURRAY", 6);
                    FocusTimerCompleted?.Invoke(doneMsg);
                }
            };
            _focusTimer.Start();

            PlaySounds("Idle.wav");
            string startMsg = _focusTaskName != null
                ? $"Focusing for {minutes} min on \"{_focusTaskName}\". You've got this!"
                : $"Focus timer set for {minutes} min. Let's get to work!";
            ShowTempMessage(startMsg, "NOTES", 4);
            return (true, startMsg);
        }

        public string StopFocusTimer()
        {
            if (_focusTimer == null || !_focusTimer.IsEnabled || DateTime.Now >= _focusEndTime)
            {
                return "No active focus timer is running.";
            }

            _focusTimer.Stop();
            string? task = _focusTaskName;
            _focusTaskName = null;
            _focusEndTime = DateTime.MinValue;

            string msg = task != null
                ? $"Stopped focus timer for \"{task}\"."
                : "Stopped focus timer.";
            ShowTempMessage(msg, "IDLE", 3);
            return msg;
        }

        public (bool IsActive, TimeSpan Remaining, string? TaskName) GetFocusStatus()
        {
            if (_focusTimer != null && _focusTimer.IsEnabled && _focusEndTime > DateTime.Now)
            {
                return (true, _focusEndTime - DateTime.Now, _focusTaskName);
            }
            return (false, TimeSpan.Zero, null);
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
                                    ShowTempMessage($"Bound {msg} to this folder!", "CUTE", 4);
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
                ApplySkin(item.Tag?.ToString() ?? "Default");
            }
        }

        public void ApplySkin(string newSkin)
        {
            if (string.IsNullOrWhiteSpace(newSkin)) return;
            newSkin = string.Concat(char.ToUpperInvariant(newSkin[0]), newSkin.AsSpan(1).ToString().ToLowerInvariant());

            currentSkin = newSkin;
            var settings = LoadFullSettings();
            settings.CurrentSkin = currentSkin;
            SaveFullSettings(settings);

            string greeting = currentSkin switch
            {
                "Green" => "Goo-morning! Let's melt some bugs.",
                "Pink" => "Fabulous! I feel... different.",
                "Girl" => "I'm ready! Let's go!",
                _ => "I'm blue dabidi dabida."
            };

            LoadAnimations();
            StartMotionLoop();
            ShowTempMessage(greeting, "IDLE", 3);
        }

        public void OpenChatWindow(string? initialThread = null, GitHubWorkflowRun? failedRun = null, string? failedLog = null)
        {
            if (_chatWindow == null || !_chatWindow.IsLoaded)
            {
                _chatWindow = new ChatWindow(this, _brainManager, _calendarWatcher, _gitHubWatcher);
                _chatWindow.Closed += (s, e) => _chatWindow = null;
            }

            if (!string.IsNullOrEmpty(initialThread))
            {
                _chatWindow.SelectThread(initialThread);
            }

            if (failedRun != null)
            {
                _chatWindow.DisplayWorkflowFailure(failedRun, failedLog);
            }

            _chatWindow.Show();
            if (_chatWindow.WindowState == WindowState.Minimized)
            {
                _chatWindow.WindowState = WindowState.Normal;
            }
            _chatWindow.Activate();
        }

        private void OnOpenChatClick(object sender, RoutedEventArgs e)
        {
            OpenChatWindow();
        }

        private void SpeechBubble_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_lastFailedRun != null)
            {
                var run = _lastFailedRun;
                var log = _lastFailedLogSnippet;
                _lastFailedRun = null;
                _lastFailedLogSnippet = null;
                OpenChatWindow(failedRun: run, failedLog: log);
            }
            else
            {
                OpenChatWindow();
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
                    _allowDesktopRoaming = settings?.AllowDesktopRoaming ?? true;
                    if (RoamingMenuItem != null) RoamingMenuItem.IsChecked = _allowDesktopRoaming;
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

                ShowTempMessage("Spawning CLI companion!", "FUNNY", 3);
            }
            catch (Exception)
            {
                ShowTempMessage("Could not open CLI terminal.", "ERROR", 3);
            }
        }

        public void TriggerOpenNotes()
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

            ShowTempMessage("Gemini key saved to my settings!", "IDLE", 3);
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

        public async void ProcessAiRequest(string prompt, string? cliRequestId = null, string? workingDir = null, string? requestedThread = null, string source = "App")
        {
            isInteracting = true;
            _isAiBusy = true;
            _interactionTimer?.Stop();
            RecordMoodEvent("CHAT");
            string response = "";

            SpeechText.Text = "Hmm... let me think...";
            SpeechText.Foreground = Brushes.Black;
            SpeechBubble.Visibility = Visibility.Visible;
            ShowSlimeReaction("THINKING", "");
            AiStatusChanged?.Invoke(SpeechText.Text);

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
                else if (!string.IsNullOrEmpty(requestedThread) && requestedThread != "General")
                {
                    activeThread = requestedThread;
                    var repoMap = _brainManager.GetBoundRepos();
                    if (repoMap.TryGetValue(activeThread, out var path) && Directory.Exists(path))
                    {
                        boundRepos.Add((activeThread, path));
                        boundPaths.Add(path);
                        targetRepoPath = path;
                    }
                }
                else
                {
                    void UseCwdRepo((string Tag, string Path) repo)
                    {
                        boundRepos.Add((repo.Tag, repo.Path));
                        boundPaths.Add(repo.Path);
                        targetRepoPath = repo.Path;
                        activeThread = repo.Tag;

                        string relCwd = Path.GetRelativePath(repo.Path, workingDir!).Replace('\\', '/');
                        if (relCwd == ".")
                        {
                            cwdHint = $"[CLI Location] The user's terminal is in the ROOT of the bound workspace '{repo.Tag}' ({repo.Path}).";
                        }
                        else
                        {
                            cwdHint = $"[CLI Location] The user's terminal is in the subfolder '{relCwd}' of the bound workspace '{repo.Tag}' ({repo.Path}). " +
                                      $"Relative file names the user mentions most likely refer to that folder (try '{relCwd}/<name>' first).";
                        }
                    }

                    var firstHashTag = words.FirstOrDefault(w => IsValidRepoHashTag(w, prompt));
                    var firstAtTag = words.FirstOrDefault(w => w.StartsWith("@")); // NYTT: Leta efter ämnen!

                    if (firstHashTag != null)
                    {
                        var cwdRepo = _brainManager.FindBoundRepoForDirectory(workingDir);
                        if (cwdRepo != null)
                        {
                            UseCwdRepo(cwdRepo.Value);
                        }
                        else
                        {
                            SpeechText.Text = $"I have no repo bound to {firstHashTag.ToLowerInvariant()}. Go to the repo's folder and run: slime bind {firstHashTag.TrimStart('#')}";
                            WriteCliResponse(cliRequestId, SpeechText.Text, true);
                            TriggerTalkingForText(SpeechText.Text, "UNSURE");
                            ShowSlimeReaction("UNSURE", "");
                            StartInteractionTimer(8);
                            return;
                        }
                    }
                    else if (firstAtTag != null)
                    {
                        // Ämne/Subject upptäckt! (t.ex. @antigravity)
                        activeThread = firstAtTag.ToLowerInvariant();
                    }
                    else
                    {
                        // Ingen tagg i prompten: står CLI:t i ett bundet repo (eller en undermapp) använder vi det
                        var cwdRepo = _brainManager.FindBoundRepoForDirectory(workingDir);
                        if (cwdRepo != null)
                        {
                            UseCwdRepo(cwdRepo.Value);
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

                AiUserMessageReceived?.Invoke(activeThread, prompt, source);

                var todoFolders = _brainManager.GetTodoScanFolders(activeThread, settings.ObsidianVaultPath);
                string standardContext = ContextManager.BuildFullContext(settings, prompt, lastStatus, todoFolders);
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
                string memoryContext = _brainManager.GetMemoryNetworkContext(prompt, activeThread);
                var queryBuilder = new System.Text.StringBuilder();

                if (!string.IsNullOrWhiteSpace(standardContext) || !string.IsNullOrWhiteSpace(tagContext) || !string.IsNullOrWhiteSpace(memoryContext))
                {
                    queryBuilder.AppendLine("[System Context]");
                    if (!string.IsNullOrWhiteSpace(memoryContext)) queryBuilder.AppendLine(memoryContext);
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

                queryBuilder.AppendLine("\n[Language Requirement]");
                queryBuilder.AppendLine("CRITICAL: You are Slime, an English-speaking AI desktop assistant. You must ALWAYS respond in English and do not use emojis in what you say. Even if the user writes, asks, or prompts in Swedish or any other language, every response, explanation, and message from you MUST be exclusively in English without emojis. Never answer in Swedish.\n");

                queryBuilder.AppendLine("\n[User Prompt]");
                queryBuilder.AppendLine(prompt);

                string fullQuery = queryBuilder.ToString();

                // Skicka till AI
                // Nollställ räknaren av verkligt skrivna ändringar, så att vi kan jämföra med vad hon påstår
                SlimeToolManager.ResetModificationCount();

                // 1. Första AI-anropet
                response = await AiService.AskSlime(fullQuery, provider, apiKey);
                response = SlimeToolManager.StripHallucinatedTurns(response);

                // 2. Kolla om hon använde verktyg (t.ex. [READ_FILE])
                string followUpPrompt;
                string cleanResponse = SlimeToolManager.ProcessResponse(response, activeThread, targetRepoPath, _brainManager, out followUpPrompt, out var pendingChecks);

                // Spara första steget i historiken
                _brainManager.SaveMessage(activeThread, "User", prompt);
                _brainManager.SaveMessage(activeThread, "Slime", cleanResponse);

                // Om hon bad om bygge/test/lint: kör det asynkront och skicka tillbaka resultatet till henne
                if (pendingChecks.Count > 0)
                {
                    SpeechText.Text = cleanResponse; // visar t.ex. "I'm running the build check... 🔨"
                    AiStatusChanged?.Invoke(cleanResponse);
                    followUpPrompt = await SlimeToolManager.RunCheckRequestsAsync(pendingChecks, targetRepoPath, activeThread, followUpPrompt);
                }

                // 3. AGENT-LOOP: Om hon bad om att få läsa en fil, skicka tillbaka filinnehållet till henne i smyg!
                var agentLog = new System.Text.StringBuilder();
                int rounds = 0;
                const int maxRounds = 8; // lista -> läsa -> ändra -> bygga -> rätta ...

                // Påstår hon en ändring som appen aldrig skrev? Då får hon en rättelse och en chans att göra det på riktigt (en gång per fråga).
                int correctionsLeft = 1;
                bool NeedsFollowUp()
                {
                    if (!string.IsNullOrEmpty(followUpPrompt)) return true;
                    if (SlimeToolManager.ShouldCorrectFalseClaim(cleanResponse, activeThread, correctionsLeft, prompt))
                    {
                        correctionsLeft--;
                        followUpPrompt = SlimeToolManager.CorrectionFor(cleanResponse, prompt);
                        return true;
                    }
                    return false;
                }

                while (NeedsFollowUp() && rounds++ < maxRounds)
                {
                    SpeechText.Text = cleanResponse; // the UI shows e.g. "I'm reading the file..."
                    AiStatusChanged?.Invoke(cleanResponse);

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
                    loopBuilder.AppendLine("- LANGUAGE: You must ALWAYS respond in English and never use emojis in what you say. Never reply in Swedish.");
                    if (SlimeToolManager.WriteFailureCount >= 3)
                    {
                        // Multiple edit failures occurred: instruct the model to stop retrying and report the issue to the user
                        loopBuilder.AppendLine("You have attempted to edit this file multiple times and the tool reported errors. Stop attempting [EDIT_FILE]. " +
                                               "Explain to the user what you were trying to change and what specific error occurred so they can assist.");
                    }
                    else if (SlimeToolManager.ModificationCount > 0)
                    {
                        // File has already been modified on disk during this request
                        bool hasCheckResult = followUpPrompt.Contains("[CHECK RESULT:");
                        bool hasCheckFailure = hasCheckResult && (
                            Regex.IsMatch(followUpPrompt, @"\[CHECK RESULT:[^\]]*-\s*FAILED") ||
                            Regex.IsMatch(followUpPrompt, @"\[CHECK RESULT:[^\]]*-\s*TIMED OUT") ||
                            followUpPrompt.Contains("[CHECK RESULT: NOT RUN"));
                        if (hasCheckFailure)
                        {
                            loopBuilder.AppendLine("A previous check (build/test/lint) FAILED with errors shown in the tool results above. Read the error messages carefully and use [EDIT_FILE] to FIX the error. " +
                                                   "Do NOT duplicate or re-insert methods that are already present in the file; only repair the compilation or test error. " +
                                                   "In C#, ensure all methods and properties are located INSIDE the class body before the class's closing '}' brace. " +
                                                   "Inspect the file lines shown above to place members correctly. After fixing, verify again with [RUN_CHECK: build].");
                        }
                        else if (hasCheckResult)
                        {
                            // All checks completed successfully
                            loopBuilder.AppendLine("The requested change has ALREADY been successfully written to disk and verified. Do NOT output [EDIT_FILE] or [WRITE_CODE] again to repeat the change. " +
                                                   "Provide your final response to the user now, briefly summarizing what you did and confirming that the build succeeded.");
                        }
                        else if (followUpPrompt.Contains("EDIT_FILE on") && followUpPrompt.Contains("FAILED"))
                        {
                            // An edit attempt failed in this turn
                            loopBuilder.AppendLine("Your last edit attempt FAILED as shown in the tool results above. Read the error message carefully. " +
                                                   "If the error says a method was placed outside a class, ensure your replacement puts the method INSIDE the class before the closing '}'. " +
                                                   "If you need to inspect the current file content on disk, use [READ_FILE]. Otherwise, ensure your [FIND] text matches the file exactly.");
                        }
                        else
                        {
                            loopBuilder.AppendLine("The requested change has ALREADY been successfully written to disk. Do NOT output [EDIT_FILE] or [WRITE_CODE] again to repeat the change. " +
                                                   "Provide your final response to the user now, briefly summarizing what you did.");
                        }
                    }
                    else if (SlimeToolManager.IsChangeRequest(prompt))
                    {
                        // Change requested but nothing has been written to disk yet (e.g. Slime only read the file)
                        loopBuilder.AppendLine("The tool results above contain the file contents or information you requested. You have NOT yet applied the requested change to disk: you MUST now output the required tool block ([EDIT_FILE] with [FIND] and [REPLACE], or [WRITE_CODE] for a new file) to apply the change. Do NOT merely describe the change in text or output bare markdown code blocks - you MUST execute it using the tool block. After editing, verify your work with [RUN_CHECK: build] if relevant.");
                    }
                    else
                    {
                        loopBuilder.AppendLine("The tool results above are the data you asked for. Answer the original user request NOW, based on them. " +
                                               "Show the user the actual information (for example the file names or the file content) in your reply - do not just say that you looked. " +
                                               "Do not call a tool again for something already in the results above. Only use another tool if you still need information that is not there. " +
                                               "The user only sees your FINAL reply, never the earlier steps, so do not mention earlier attempts or errors you already fixed, and do not apologise for them. Just say what is true now.");
                    }

                    string loopQuery = loopBuilder.ToString();

                    response = await AiService.AskSlime(loopQuery, provider, apiKey);
                    response = SlimeToolManager.StripHallucinatedTurns(response);

                    // Processa nya verktyg i svaret; en ny uppföljning startar nästa runda
                    cleanResponse = SlimeToolManager.ProcessResponse(response, activeThread, targetRepoPath, _brainManager, out followUpPrompt, out pendingChecks);

                    _brainManager.SaveMessage(activeThread, "Slime", cleanResponse);

                    if (pendingChecks.Count > 0)
                    {
                        SpeechText.Text = cleanResponse;
                        AiStatusChanged?.Invoke(cleanResponse);
                        followUpPrompt = await SlimeToolManager.RunCheckRequestsAsync(pendingChecks, targetRepoPath, activeThread, followUpPrompt);
                    }
                }

                // 3b. Om hon fortfarande påstår något som aldrig skrevs: säg det rakt ut, och låt henne veta det i historiken
                // Till CLI:t: hela händelsekedjan (läste, ändrade, byggde, resultat) följt av hennes svar
                string cliText = SlimeToolManager.ComposeCliText(cleanResponse);

                string? warning = SlimeToolManager.WarningFor(cleanResponse, prompt, activeThread);
                if (warning != null)
                {
                    _brainManager.SaveMessage(activeThread, "System", SlimeToolManager.NoteFor(cleanResponse, prompt));
                    cleanResponse += "\n\n" + warning;
                    cliText += "\n\n" + warning;
                }

                // 4. Visa det slutgiltiga resultatet i appen
                SpeechText.Text = cleanResponse;
                if (!string.IsNullOrWhiteSpace(cleanResponse))
                {
                    _talkingUntil = DateTime.Now.AddMilliseconds(Math.Clamp(cleanResponse.Length * 25, 800, 2200));
                }
                AiResponseReceived?.Invoke(activeThread, cleanResponse);
                WriteCliResponse(cliRequestId, cliText);
                ShowSlimeReaction("IDLE", "");
                PlaySounds("Idle.wav");
            }
            catch (Exception ex)
            {
                SpeechText.Text = $"Brain freeze! {ex.Message}";
                AiErrorOccurred?.Invoke(ex.Message);
                WriteCliResponse(cliRequestId, SpeechText.Text, true);
                TriggerTalkingForText(SpeechText.Text, "ERROR");
                ShowSlimeReaction("ERROR", "");
                SpeechText.Foreground = Brushes.Red;
            }
            finally
            {
                _isAiBusy = false;
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
                    TriggerTalkingForText(message, status);
                }

                string upper = (status ?? "").ToUpperInvariant();
                if (upper == "HURRAY")
                {
                    SpawnEmoteBurst("🎉", 3, spin: true);
                    SpawnEmoteBurst("✨", 2);
                }
                else if (upper == "STREAK")
                {
                    SpawnEmoteBurst("✨", 3);
                }
                else if (upper == "CUTE")
                {
                    SpawnEmoteBurst("💖", 2);
                }
                else if (upper == "SCARED")
                {
                    SpawnEmoteBurst("💫", 3, spin: true);
                }

                _ = PlayAnimationLoop(upper);
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
                _activityWatcher.ActiveRepoPath = currentSettings.ReposRootPath;
                _gitHubWatcher.SetActiveRepo(currentSettings.ReposRootPath);

                ShowTempMessage("Git Repositories folder linked!", "NOTES", 3);
                PlaySounds("Idle.wav");
            }
        }

        private void OnSetGitHubTokenClick(object sender, RoutedEventArgs e)
        {
            var currentSettings = LoadFullSettings();
            string token = SlimeInputDialog.Show("GitHub Configuration", "Enter your GitHub Personal Access Token (PAT):", currentSettings.GitHubToken);

            if (!string.IsNullOrWhiteSpace(token) && token != "Enter your Key here!")
            {
                currentSettings.GitHubToken = token.Trim();
                SaveFullSettings(currentSettings);
                _gitHubWatcher.GitHubToken = currentSettings.GitHubToken;
                ShowTempMessage("GitHub token saved!", "IDLE", 3);
                PlaySounds("Idle.wav");
            }
        }

        private async void OnAutoDetectGitHubTokenClick(object sender, RoutedEventArgs e)
        {
            string? ghToken = GitHubService.TryGetGhCliToken();
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                var currentSettings = LoadFullSettings();
                currentSettings.GitHubToken = ghToken;
                SaveFullSettings(currentSettings);
                _gitHubWatcher.GitHubToken = ghToken;
                ShowTempMessage("Auto-detected token from GitHub CLI!", "HURRAY", 4);
                PlaySounds("Idle.wav");
            }
            else
            {
                ShowTempMessage("Could not detect token from `gh auth token`. Make sure gh is logged in!", "WARNING", 5);
                PlaySounds("Warning.wav");
            }
        }

        private async void OnTestGitHubConnectionClick(object sender, RoutedEventArgs e)
        {
            ShowTempMessage("Testing GitHub connection...", "THINKING", 3);
            var currentSettings = LoadFullSettings();
            string? token = !string.IsNullOrWhiteSpace(currentSettings.GitHubToken) ? currentSettings.GitHubToken : null;
            var (ok, user, rate) = await _gitHubWatcher.Service.TestConnectionAsync(token);
            if (ok)
            {
                ShowTempMessage($"Connected as @{user}! (Rate remaining: {rate})", "HURRAY", 5);
                PlaySounds("Idle.wav");
            }
            else
            {
                ShowTempMessage($"GitHub connection failed: {user}", "ERROR", 5);
                PlaySounds("Warning.wav");
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

                ShowTempMessage("Slime is analyzing dataset...", "THINKING", 4);
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


        private void RoamingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _allowDesktopRoaming = RoamingMenuItem.IsChecked;
            var settings = LoadFullSettings();
            settings.AllowDesktopRoaming = _allowDesktopRoaming;
            SaveFullSettings(settings);

            string msg = _allowDesktopRoaming
                ? "Desktop roaming enabled! I might hop around when I'm bored."
                : "Desktop roaming disabled. I'll stay right where you put me!";
            ShowTempMessage(msg, "CUTE", 3);
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

        // TODO [Eye Tracking PNG Layer Split Specification]:
        // Currently, all sprite frames in Images/<Skin>/<State>/*.png are flat single-layer PNGs
        // with the eyes baked directly into the body/character bitmap.
        // To enable true pupil/iris mouse-tracking in the future, split the IDLE (and optionally other) sprites
        // into 3 transparent PNG layers exported at the exact same pixel dimensions (e.g. 1024x1024):
        //   1. <frame>_base.png    - Body, face, mouth, and empty eye sockets / sclera base (NO pupils/irises).
        //   2. <frame>_eyes.png    - ONLY the pupils/irises and eye specular highlights on a transparent canvas.
        //                            In WPF, this Image layer will be placed between base and overlay and shifted
        //                            by ±4..8 px along X/Y toward the cursor position read via GetCursorPos.
        //   3. <frame>_overlay.png - Eyelashes, upper eyelids, eyebrows, blush, and hair bangs that sit in front
        //                            of the eyes so the moving pupils clip naturally under the eyelids/hair.
        // Recommended folder structure per skin: Images/<Skin>/IDLE/Layers/01_base.png, 01_eyes.png, 01_overlay.png.

        private void StartMotionLoop()
        {
            _motionTimer?.Stop();
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

            // Continuous breathing – runs across all states, scaled by Phase 5 Mood/Energy multiplier
            double breathFreq = 1.6 * _currentBreathingMultiplier;
            double breathScale = 1.0 + Math.Sin(t * breathFreq) * 0.015;
            double breathY = Math.Sin(t * breathFreq) * 1.5;

            // Impact pulse: damped spring bouncing back toward 0
            const double stiffness = 220.0, damping = 18.0, dt = 0.016;
            double force = -stiffness * _impactOffset - damping * _impactVelocity;
            _impactVelocity += force * dt;
            _impactOffset += _impactVelocity * dt;

            // Secondary sway & drag pendulum physics (SkewTransform keeps feet anchored on ground)
            const double skewStiffness = 140.0, skewDamping = 12.5;
            double skewForce = -skewStiffness * _skewOffset - skewDamping * _skewVelocity;
            _skewVelocity += skewForce * dt;
            _skewOffset += _skewVelocity * dt;
            _skewOffset = Math.Clamp(_skewOffset, -7.5, 7.5);

            double organicIdleSway = (_isAsleep || _isAfkSleeping)
                ? Math.Sin(t * 0.45) * 0.25
                : Math.Sin(t * 0.75) * 0.45;

            // Gravity drop & bounce physics after drag release
            if (_jumpOffsetY < 0 || Math.Abs(_jumpVelocityY) > 0.5)
            {
                const double gravity = 520.0;
                _jumpVelocityY += gravity * dt;
                _jumpOffsetY += _jumpVelocityY * dt;

                if (_jumpOffsetY >= 0)
                {
                    _jumpOffsetY = 0;
                    if (_jumpVelocityY > 45.0)
                    {
                        // Landing impact squash + small bounce
                        TriggerImpact(Math.Min(2.2, _jumpVelocityY / 95.0));
                        _jumpVelocityY = -_jumpVelocityY * 0.28;
                    }
                    else
                    {
                        _jumpVelocityY = 0;
                    }
                }
            }

            // Talking bounce when Slime is speaking (no typewriter text)
            double talkBob = 0;
            if (DateTime.Now < _talkingUntil && !_isAsleep && !_isAfkSleeping)
            {
                talkBob = Math.Abs(Math.Sin(t * 15.0));
            }

            // Occasional idle flavor tilt (only in IDLE, no new art required)
            if (DateTime.Now >= _nextIdleFlavorAt && !isInteracting && _currentPlayingState == "IDLE")
            {
                _ = PlayIdleFlavor();
                _nextIdleFlavorAt = DateTime.Now.AddSeconds(rng.Next(20, 45));
            }

            // Autonomous desktop roaming hops when idle and enabled
            if (_allowDesktopRoaming && !_isRoamingHopActive && !_isDraggingWindow &&
                !isInteracting && !_isAsleep && !_isAfkSleeping && _currentPlayingState == "IDLE" &&
                DateTime.Now >= _nextRoamingHopAt)
            {
                _nextRoamingHopAt = DateTime.Now.AddSeconds(rng.Next(35, 70));
                _ = PerformRoamingHopAsync();
            }

            double finalScaleX = breathScale - _impactOffset * 0.4 - talkBob * 0.018;
            double finalScaleY = breathScale + _impactOffset * 0.6 + talkBob * 0.032;
            double finalY = breathY + _jumpOffsetY - talkBob * 3.0;

            double dragLagX = _skewOffset * 1.15;

            SlimeScale.ScaleX = finalScaleX;
            SlimeScale.ScaleY = finalScaleY;
            SlimeSkew.AngleX = organicIdleSway + _skewOffset;
            SlimeTranslate.Y = finalY;
            SlimeTranslate.X = _flavorX + dragLagX;
            SlimeRotate.Angle = _flavorAngle * 0.4;

            // Dynamic Ground Shadow reacts to height and squash
            if (SlimeShadow != null && ShadowScale != null && ShadowTranslate != null)
            {
                double heightLift = Math.Max(0.0, -finalY);
                double shadowWidthFactor = Math.Clamp(finalScaleX - heightLift * 0.012, 0.55, 1.35);
                ShadowScale.ScaleX = shadowWidthFactor;
                ShadowScale.ScaleY = Math.Clamp(1.0 - heightLift * 0.01, 0.6, 1.2);
                ShadowTranslate.X = _flavorX + dragLagX;
                SlimeShadow.Opacity = Math.Clamp(0.85 - heightLift * 0.018, 0.25, 0.9);
            }

            // Periodic floating Zzz particles while sleeping
            if ((_isAsleep || _isAfkSleeping || _currentPlayingState == "SLEEP") && DateTime.Now >= _nextSleepZzzAt)
            {
                SpawnEmoteBurst("💤", 1, spin: false, isSlowFloat: true);
                _nextSleepZzzAt = DateTime.Now.AddSeconds(2.2);
            }

            UpdatePaperDollMotion(t, dt);
            UpdateEmoteParticles(dt);
        }

        private static BitmapImage? LoadFrozenBitmap(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static PaperDollProfile? LoadPaperDollLayers(string stateDir)
        {
            var paths = PaperDollResolver.ResolveLayers(stateDir);
            if (paths == null) return null;

            var baseImg = LoadFrozenBitmap(paths.Base);
            if (baseImg == null) return null;

            BitmapImage? LoadOptional(string? path) => path != null ? LoadFrozenBitmap(path) : null;

            return new PaperDollProfile
            {
                Base = baseImg,
                EyeWhite = LoadOptional(paths.EyeWhite),
                Eyes = LoadOptional(paths.Eyes),
                EyesBlink = LoadOptional(paths.EyesBlink),
                Nose = LoadOptional(paths.Nose),
                MouthClosed = LoadOptional(paths.MouthClosed),
                MouthOpen = LoadOptional(paths.MouthOpen),
                MouthSmile = LoadOptional(paths.MouthSmile),
                Eyebrows = LoadOptional(paths.Eyebrows),
                HairLeft = LoadOptional(paths.HairLeft),
                HairMid = LoadOptional(paths.HairMid),
                HairRight = LoadOptional(paths.HairRight),
                Overlay = LoadOptional(paths.Overlay),
                OverlayFrames = paths.OverlayFrames.Select(LoadFrozenBitmap).OfType<BitmapImage>().ToList(),
                MotionGroups = paths.MotionGroups
                    .Select(g => g.Select(LoadFrozenBitmap).OfType<BitmapImage>().ToList())
                    .Where(g => g.Count > 0)
                    .ToList()
            };
        }

        private void ActivatePaperDoll(PaperDollProfile pd)
        {
            _activePaperDoll = pd;
            _isPaperDollBlinking = false;
            _lastMouthMode = -1;
            _lastOverlayFrameIdx = -1;
            Array.Fill(_lastMotionFrameIdx, -1);
            _eyeOffsetX = 0;
            _eyeOffsetY = 0;
            if (PdEyesTranslate != null)
            {
                PdEyesTranslate.X = 0;
                PdEyesTranslate.Y = 0;
            }

            if (SlimeImageBack != null)
            {
                SlimeImageBack.BeginAnimation(OpacityProperty, null);
                SlimeImageBack.Opacity = 0;
                SlimeImageBack.Source = null;
                SlimeImageBack.Visibility = Visibility.Collapsed;
            }
            SlimeImage.BeginAnimation(OpacityProperty, null);
            SlimeImage.Source = null;
            SlimeImage.Visibility = Visibility.Collapsed;

            if (PaperDollHost == null) return;
            PdBaseImage.Source = pd.Base;
            PdEyeWhiteImage.Source = pd.EyeWhite;
            PdEyesImage.Source = pd.Eyes;
            PdNoseImage.Source = pd.Nose;
            PdMouthImage.Source = pd.MouthSmile ?? pd.MouthClosed ?? pd.MouthOpen;
            PdBlinkImage.Source = pd.EyesBlink;
            PdBlinkImage.Visibility = Visibility.Collapsed;
            PdEyeWhiteImage.Visibility = Visibility.Visible;
            PdEyesImage.Visibility = Visibility.Visible;
            PdEyebrowsImage.Source = pd.Eyebrows;
            PdHairLeftImage.Source = pd.HairLeft;
            PdHairMidImage.Source = pd.HairMid;
            PdHairRightImage.Source = pd.HairRight;
            PdOverlayImage.Source = pd.Overlay;
            if (PdEffectImage != null)
            {
                PdEffectImage.Source = pd.OverlayFrames.Count > 0 ? pd.OverlayFrames[0] : null;
            }
            if (PdMotion1Image != null) PdMotion1Image.Source = pd.MotionGroups.Count > 0 ? pd.MotionGroups[0][0] : null;
            if (PdMotion2Image != null) PdMotion2Image.Source = pd.MotionGroups.Count > 1 ? pd.MotionGroups[1][0] : null;
            if (PdMotion3Image != null) PdMotion3Image.Source = pd.MotionGroups.Count > 2 ? pd.MotionGroups[2][0] : null;

            PaperDollHost.Visibility = Visibility.Visible;
        }

        private void DeactivatePaperDoll()
        {
            if (_activePaperDoll == null) return;
            _activePaperDoll = null;
            _isPaperDollBlinking = false;
            _lastMouthMode = -1;

            if (PaperDollHost != null) PaperDollHost.Visibility = Visibility.Collapsed;
            if (SlimeImageBack != null) SlimeImageBack.Visibility = Visibility.Visible;
            SlimeImage.Visibility = Visibility.Visible;
        }

        private void SetPaperDollBlink(bool blinking)
        {
            if (_activePaperDoll == null || _activePaperDoll.EyesBlink == null || PaperDollHost == null) return;
            _isPaperDollBlinking = blinking;
            PdBlinkImage.Visibility = blinking ? Visibility.Visible : Visibility.Collapsed;
            PdEyeWhiteImage.Visibility = blinking ? Visibility.Collapsed : Visibility.Visible;
            PdEyesImage.Visibility = blinking ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdatePaperDollMotion(double t, double dt)
        {
            if (_activePaperDoll == null || PaperDollHost == null || PaperDollHost.Visibility != Visibility.Visible) return;

            // 1. Cursor Eye-Tracking (only active in IDLE so expression-specific poses keep their intended gaze)
            double targetEyeX = 0;
            double targetEyeY = 0;
            if (_currentPlayingState == "IDLE" && !_isAsleep && !_isAfkSleeping && !_isPaperDollBlinking && GetCursorPos(out var cursorPt))
            {
                try
                {
                    var source = PresentationSource.FromVisual(this);
                    double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                    double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                    double eyeScreenX = (Left + Width * 0.52) * dpiX;
                    double eyeScreenY = (Top + Height * 0.42) * dpiY;

                    double dx = cursorPt.X - eyeScreenX;
                    double dy = cursorPt.Y - eyeScreenY;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist > 12.0)
                    {
                        double normX = Math.Clamp(dx / 260.0, -1.0, 1.0);
                        double normY = Math.Clamp(dy / 260.0, -1.0, 1.0);
                        targetEyeX = normX * 2.2;
                        targetEyeY = normY * 1.6;
                    }
                }
                catch { }
            }

            double lerp = Math.Min(1.0, dt * 10.0);
            _eyeOffsetX += (targetEyeX - _eyeOffsetX) * lerp;
            _eyeOffsetY += (targetEyeY - _eyeOffsetY) * lerp;
            if (PdEyesTranslate != null)
            {
                PdEyesTranslate.X = _eyeOffsetX;
                PdEyesTranslate.Y = _eyeOffsetY;
            }

            // 2. Lip-sync while talking, smile when happy, closed otherwise
            if (PdMouthImage != null)
            {
                bool isTalking = DateTime.Now < _talkingUntil && _currentPlayingState != "SLEEP" && !_isAsleep && !_isAfkSleeping;
                int desiredMode;
                BitmapImage? desiredBmp = null;

                if (isTalking && _activePaperDoll.MouthOpen != null && (_activePaperDoll.MouthClosed != null || _activePaperDoll.MouthSmile != null))
                {
                    bool openPhase = ((int)(t * 8.0) % 2) == 0;
                    desiredMode = openPhase ? 1 : 0;
                    desiredBmp = openPhase
                        ? _activePaperDoll.MouthOpen
                        : (_activePaperDoll.MouthClosed ?? _activePaperDoll.MouthSmile);
                }
                else if (_currentMoodValue >= 55 && _activePaperDoll.MouthSmile != null)
                {
                    desiredMode = 2;
                    desiredBmp = _activePaperDoll.MouthSmile;
                }
                else
                {
                    desiredMode = 0;
                    desiredBmp = _activePaperDoll.MouthClosed ?? _activePaperDoll.MouthSmile ?? _activePaperDoll.MouthOpen;
                }

                if (desiredMode != _lastMouthMode && desiredBmp != null)
                {
                    _lastMouthMode = desiredMode;
                    PdMouthImage.Source = desiredBmp;
                }
            }

            // 3. Subtle secondary hair sway
            double hairBaseSway = Math.Sin(t * 1.4 - 0.5) * 1.1 + _skewOffset * 0.22;
            if (PdHairLeftRotate != null) PdHairLeftRotate.Angle = hairBaseSway;
            if (PdHairMidRotate != null) PdHairMidRotate.Angle = hairBaseSway * 0.7;
            if (PdHairRightRotate != null) PdHairRightRotate.Angle = hairBaseSway * 1.05;

            // 4. Animated overlay frames (e.g. warning-1/2/3, screen-flicker-1/2/3, heart-1/2/3, drops-1/2)
            if (PdEffectImage != null && _activePaperDoll.OverlayFrames.Count > 1)
            {
                int frameIdx = (int)(t * 5.5) % _activePaperDoll.OverlayFrames.Count;
                if (frameIdx != _lastOverlayFrameIdx)
                {
                    _lastOverlayFrameIdx = frameIdx;
                    PdEffectImage.Source = _activePaperDoll.OverlayFrames[frameIdx];
                }
            }

            // 5. Independent slime-motion drip pairs (slimemotion-1-1/1-2, 2-1/2-2, 3-1/3-2)
            Image?[] motionTargets = [PdMotion1Image, PdMotion2Image, PdMotion3Image];
            for (int g = 0; g < Math.Min(3, _activePaperDoll.MotionGroups.Count); g++)
            {
                var group = _activePaperDoll.MotionGroups[g];
                var targetImg = motionTargets[g];
                if (targetImg == null || group.Count <= 1) continue;

                int idx = (int)((t + g * 0.45) * 1.4) % group.Count;
                if (idx != _lastMotionFrameIdx[g])
                {
                    _lastMotionFrameIdx[g] = idx;
                    targetImg.Source = group[idx];
                }
            }
        }

        private async Task PerformRoamingHopAsync()
        {
            if (_isRoamingHopActive || _isDraggingWindow || _isAsleep || _isAfkSleeping) return;
            _isRoamingHopActive = true;

            try
            {
                var workArea = SystemParameters.WorkArea;
                double minLeft = workArea.Left + 10;
                double maxLeft = Math.Max(minLeft, workArea.Right - Width - 10);

                double step = rng.Next(28, 62) * (rng.Next(0, 2) == 0 ? -1 : 1);
                double targetLeft = Left + step;

                // Reverse direction if near screen edges
                if (targetLeft < minLeft || targetLeft > maxLeft)
                {
                    step = -step;
                    targetLeft = Math.Clamp(Left + step, minLeft, maxLeft);
                }

                double startLeft = Left;
                double actualStep = targetLeft - startLeft;
                if (Math.Abs(actualStep) < 8) return;

                // Anticipation crouch before jump
                TriggerImpact(0.9);
                _skewVelocity += actualStep > 0 ? -25.0 : 25.0;
                await Task.Delay(90);

                const int jumpDurationMs = 380;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < jumpDurationMs && !_isDraggingWindow && !_isAsleep)
                {
                    double p = Math.Min(1.0, sw.ElapsedMilliseconds / (double)jumpDurationMs);
                    double easedX = 1 - Math.Pow(1 - p, 2);
                    Left = startLeft + actualStep * easedX;

                    // Parabolic jump arc
                    _jumpOffsetY = -22.0 * 4.0 * p * (1.0 - p);
                    await Task.Delay(16);
                }

                _jumpOffsetY = 0;
                TriggerImpact(1.35);
            }
            catch { }
            finally
            {
                _jumpOffsetY = 0;
                _isRoamingHopActive = false;
            }
        }

        private void SpawnEmoteBurst(string symbol, int count = 2, bool spin = false, bool isSlowFloat = false)
        {
            if (EmoteCanvas == null) return;

            // Cap active particles to keep rendering lightweight
            while (_activeParticles.Count > 18)
            {
                EmoteCanvas.Children.Remove(_activeParticles[0].Element);
                _activeParticles.RemoveAt(0);
            }

            for (int i = 0; i < count; i++)
            {
                var scale = new ScaleTransform(0.6, 0.6);
                var rotate = new RotateTransform(0);
                var translate = new TranslateTransform(0, 0);
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(scale);
                transformGroup.Children.Add(rotate);
                transformGroup.Children.Add(translate);

                var tb = new TextBlock
                {
                    Text = symbol,
                    FontSize = isSlowFloat ? 18 : rng.Next(17, 23),
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = transformGroup,
                    Opacity = 1.0
                };

                double startX = isSlowFloat
                    ? 115 + rng.Next(-12, 18)
                    : 85 + rng.Next(-35, 45);
                double startY = isSlowFloat
                    ? 35 + rng.Next(-8, 8)
                    : 30 + rng.Next(-15, 25);

                double vx = isSlowFloat
                    ? rng.NextDouble() * 10 - 2
                    : (rng.NextDouble() - 0.5) * 55;
                double vy = isSlowFloat
                    ? -22 - rng.NextDouble() * 8
                    : -45 - rng.NextDouble() * 35;

                translate.X = startX;
                translate.Y = startY;

                EmoteCanvas.Children.Add(tb);
                _activeParticles.Add(new EmoteParticle
                {
                    Element = tb,
                    Translate = translate,
                    Rotate = rotate,
                    Scale = scale,
                    X = startX,
                    Y = startY,
                    Vx = vx,
                    Vy = vy,
                    SpinSpeed = spin ? (rng.Next(0, 2) == 0 ? -240 : 240) : 0,
                    WavePhase = rng.NextDouble() * Math.PI * 2,
                    WaveAmplitude = isSlowFloat ? 12.0 : 5.0,
                    Age = 0,
                    Lifetime = isSlowFloat ? 2.1 : 1.15
                });
            }
        }

        private void UpdateEmoteParticles(double dt)
        {
            if (_activeParticles.Count == 0) return;

            for (int i = _activeParticles.Count - 1; i >= 0; i--)
            {
                var p = _activeParticles[i];
                p.Age += dt;

                if (p.Age >= p.Lifetime)
                {
                    EmoteCanvas.Children.Remove(p.Element);
                    _activeParticles.RemoveAt(i);
                    continue;
                }

                double progress = p.Age / p.Lifetime;
                p.X += p.Vx * dt;
                p.Y += p.Vy * dt;

                double waveOffset = Math.Sin(p.WavePhase + p.Age * 4.5) * p.WaveAmplitude;
                p.Translate.X = p.X + waveOffset;
                p.Translate.Y = p.Y;

                if (Math.Abs(p.SpinSpeed) > 0.1)
                {
                    p.Rotate.Angle += p.SpinSpeed * dt;
                }

                double scaleVal = progress < 0.2
                    ? 0.6 + (progress / 0.2) * 0.5
                    : 1.1 - ((progress - 0.2) / 0.8) * 0.25;
                p.Scale.ScaleX = scaleVal;
                p.Scale.ScaleY = scaleVal;

                p.Element.Opacity = progress < 0.65
                    ? 1.0
                    : Math.Max(0.0, 1.0 - (progress - 0.65) / 0.35);
            }
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
        public void SaveFullSettings(SlimeSettings settings)
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

        public SlimeSettings LoadFullSettings()
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
                                        ShowTempMessage($"Bound {msg} to this folder!", "CUTE", 4);
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
                                WriteCliResponse(requestId, "Bind failed: invalid command.", true);
                            }
                        }
                        else if (!string.IsNullOrEmpty(prompt))
                        {
                            Dispatcher.Invoke(() => ProcessAiRequest(prompt, requestId, workingDir, source: "CLI"));
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
        public string GitHubToken { get; set; } = "";
        public string ObsidianVaultPath { get; set; } = "";
        public string ReposRootPath { get; set; } = "";
        public bool AutoStartWithWindows { get; set; } = false;
        public bool AllowDesktopRoaming { get; set; } = true;
    }

    public class PaperDollProfile
    {
        public required BitmapImage Base { get; init; }
        public BitmapImage? EyeWhite { get; init; }
        public BitmapImage? Eyes { get; init; }
        public BitmapImage? EyesBlink { get; init; }
        public BitmapImage? Nose { get; init; }
        public BitmapImage? MouthClosed { get; init; }
        public BitmapImage? MouthOpen { get; init; }
        public BitmapImage? MouthSmile { get; init; }
        public BitmapImage? Eyebrows { get; init; }
        public BitmapImage? HairLeft { get; init; }
        public BitmapImage? HairMid { get; init; }
        public BitmapImage? HairRight { get; init; }
        public BitmapImage? Overlay { get; init; }
        public List<BitmapImage> OverlayFrames { get; init; } = [];
        public List<List<BitmapImage>> MotionGroups { get; init; } = [];
    }

    public class AnimationProfile
    {
        public List<string> Frames { get; set; } = [];
        public int FrameDelayMs { get; set; } = 150;
        public int LoopDelayMs { get; set; } = 0;
        public PaperDollProfile? PaperDoll { get; set; }
    }
}