using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SlimeHelper
{
    public partial class ChatWindow : Window, INotifyPropertyChanged
    {
        private readonly MainWindow _mainWindow;
        private readonly SlimeBrainManager _brainManager;
        private readonly CalendarWatcher? _calendarWatcher;

        public ObservableCollection<ChatUiMessage> Messages { get; } = new();

        public string CurrentThread { get; private set; } = "General";

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(IsNotBusy));
                    UpdateBusyUI();
                }
            }
        }

        public bool IsNotBusy => !IsBusy;

        public ChatWindow(MainWindow mainWindow, SlimeBrainManager brainManager, CalendarWatcher? calendarWatcher = null)
        {
            InitializeComponent();
            DataContext = this;

            _mainWindow = mainWindow;
            _brainManager = brainManager;
            _calendarWatcher = calendarWatcher;

            // Subscribe to MainWindow AI pipeline events
            _mainWindow.AiStatusChanged += OnAiStatusChanged;
            _mainWindow.AiResponseReceived += OnAiResponseReceived;
            _mainWindow.AiErrorOccurred += OnAiErrorOccurred;
            _mainWindow.AiUserMessageReceived += OnAiUserMessageReceived;

            RefreshThreads();
            LoadHistoryForThread(CurrentThread);

            Loaded += (s, e) =>
            {
                InputTextBox.Focus();
                ScrollToBottom();
            };
        }

        public void SelectThread(string threadName)
        {
            if (string.IsNullOrWhiteSpace(threadName)) return;

            CurrentThread = threadName;
            RefreshThreads();
            ThreadComboBox.SelectedItem = threadName;
            LoadHistoryForThread(threadName);
        }

        public void RefreshThreads()
        {
            var threads = _brainManager.GetAllThreads();
            ThreadComboBox.ItemsSource = threads;

            if (threads.Contains(CurrentThread))
            {
                ThreadComboBox.SelectedItem = CurrentThread;
            }
            else if (threads.Count > 0)
            {
                ThreadComboBox.SelectedIndex = 0;
                CurrentThread = threads[0];
            }

            UpdateThreadHint();
        }

        private void UpdateThreadHint()
        {
            var repoMap = _brainManager.GetBoundRepos();
            if (repoMap.TryGetValue(CurrentThread, out var path))
            {
                RepoPathHint.Text = $"📁 {path}";
                RepoPathHint.ToolTip = path;
            }
            else
            {
                RepoPathHint.Text = "";
                RepoPathHint.ToolTip = null;
            }
        }

        public void LoadHistoryForThread(string threadId)
        {
            Messages.Clear();

            var history = _brainManager.GetHistoryMessages(threadId);
            if (history.Count == 0)
            {
                AddSystemMessage($"Start a new conversation in [{threadId}]. Ask anything or type /help!");
            }
            else
            {
                foreach (var msg in history)
                {
                    if (msg.Role == "User")
                    {
                        Messages.Add(new ChatUiMessage
                        {
                            Sender = "You",
                            Text = msg.Content,
                            IsUser = true
                        });
                    }
                    else if (msg.Role == "Slime")
                    {
                        string cleanText = SlimeToolManager.RemoveStatusLines(msg.Content);
                        if (!string.IsNullOrWhiteSpace(cleanText))
                        {
                            Messages.Add(new ChatUiMessage
                            {
                                Sender = "Slime",
                                Text = cleanText,
                                IsUser = false
                            });
                        }
                    }
                }
            }

            ScrollToBottom();
        }

        private async void SendMessage()
        {
            string input = InputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input) || IsBusy) return;

            InputTextBox.Text = "";
            PlaceholderText.Visibility = Visibility.Visible;

            // Handle Slash Commands
            if (input.StartsWith("/"))
            {
                await HandleCommandAsync(input);
                return;
            }

            // Normal AI Message
            Messages.Add(new ChatUiMessage
            {
                Sender = "You",
                Text = input,
                IsUser = true,
                Timestamp = DateTime.Now.ToString("HH:mm")
            });

            ScrollToBottom();

            IsBusy = true;
            StatusTextBlock.Text = "Hmm... let me think...";
            StatusBanner.Visibility = Visibility.Visible;

            // Determine workingDir from bound repo if available
            string? workingDir = null;
            var repoMap = _brainManager.GetBoundRepos();
            if (repoMap.TryGetValue(CurrentThread, out var boundPath))
            {
                workingDir = boundPath;
            }

            // Pass source "ChatWindow" to avoid duplicate user echo
            _ = Task.Run(() =>
            {
                Dispatcher.Invoke(() =>
                {
                    _mainWindow.ProcessAiRequest(input, workingDir: workingDir, requestedThread: CurrentThread, source: "ChatWindow");
                });
            });
        }

        private async Task HandleCommandAsync(string commandText)
        {
            string cmd = commandText.Trim();
            string lower = cmd.ToLowerInvariant();

            if (lower == "/help")
            {
                AddSystemMessage(
                    "💡 Slime Assistant Commands:\n\n" +
                    "• /help — Show this help text\n" +
                    "• /clear — Clear current chat screen\n" +
                    "• /skin <color> — Change skin (Blue/Default, Green, Pink, Girl)\n" +
                    "• /cal <event> — Add event to Google Calendar (e.g. /cal Meeting tomorrow at 14)\n" +
                    "• /todo <task> — Add task to slime_notes.md\n" +
                    "• /notes — Open Obsidian or workspace notes\n" +
                    "• /bind <#tag> [path] — Bind a repository folder to a hashtag\n\n" +
                    "✨ You can also mention #repo or @topic anytime in normal questions!");
                return;
            }

            if (lower == "/clear")
            {
                Messages.Clear();
                AddSystemMessage("Chat view cleared. (History is safely kept in Slime's memory bank)");
                return;
            }

            if (lower.StartsWith("/skin"))
            {
                var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    _mainWindow.ApplySkin(parts[1]);
                    AddSystemMessage($"🎨 Changed skin to {parts[1]}!");
                }
                else
                {
                    AddSystemMessage("Usage: /skin <Default | Green | Pink | Girl>");
                }
                return;
            }

            if (lower.StartsWith("/cal"))
            {
                var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && _calendarWatcher != null)
                {
                    string eventDetails = parts[1];
                    AddSystemMessage($"📅 Adding \"{eventDetails}\" to Google Calendar...");
                    bool ok = await _calendarWatcher.AddEventAsync(eventDetails);
                    if (ok)
                    {
                        AddSystemMessage($"✔ Successfully scheduled: \"{eventDetails}\"");
                    }
                    else
                    {
                        AddSystemMessage("❌ Failed to add event to Google Calendar. Check credentials.");
                    }
                }
                else
                {
                    AddSystemMessage("Usage: /cal <event title and optional time>");
                }
                return;
            }

            if (lower.StartsWith("/todo"))
            {
                var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    string task = parts[1];
                    string targetFolder = Directory.GetCurrentDirectory();
                    var repoMap = _brainManager.GetBoundRepos();
                    if (repoMap.TryGetValue(CurrentThread, out var boundPath))
                    {
                        targetFolder = boundPath;
                    }

                    string notesPath = Path.Combine(targetFolder, "slime_notes.md");
                    File.AppendAllText(notesPath, $"- [ ] {task}\n");
                    AddSystemMessage($"✔ Added to To-Do: \"{task}\" (in {Path.GetFileName(notesPath)})");
                }
                else
                {
                    AddSystemMessage("Usage: /todo <your task description>");
                }
                return;
            }

            if (lower == "/notes")
            {
                _mainWindow.TriggerOpenNotes();
                AddSystemMessage("📝 Opening your notes folder...");
                return;
            }

            if (lower.StartsWith("/bind"))
            {
                var parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string tag = parts[1];
                    string path = parts.Length > 2 ? parts[2] : Directory.GetCurrentDirectory();

                    var (ok, msg) = _brainManager.BindRepo(tag, path);
                    if (ok)
                    {
                        AddSystemMessage($"✔ Bound {msg} → {path}");
                        RefreshThreads();
                    }
                    else
                    {
                        AddSystemMessage($"❌ Bind failed: {msg}");
                    }
                }
                else
                {
                    AddSystemMessage("Usage: /bind <#tag> [folder_path]");
                }
                return;
            }

            AddSystemMessage($"Unknown command: {cmd}. Type /help for available commands.");
        }

        private void AddSystemMessage(string text)
        {
            Messages.Add(new ChatUiMessage
            {
                Sender = "System",
                Text = text,
                IsSystem = true,
                Timestamp = DateTime.Now.ToString("HH:mm")
            });
            ScrollToBottom();
        }

        private void OnAiStatusChanged(string status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = status;
                StatusBanner.Visibility = Visibility.Visible;
                ScrollToBottom();
            });
        }

        private void OnAiResponseReceived(string thread, string response)
        {
            Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                StatusBanner.Visibility = Visibility.Collapsed;

                Messages.Add(new ChatUiMessage
                {
                    Sender = "Slime",
                    Text = response,
                    IsUser = false,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                });

                ScrollToBottom();
            });
        }

        private void OnAiErrorOccurred(string error)
        {
            Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                StatusBanner.Visibility = Visibility.Collapsed;

                Messages.Add(new ChatUiMessage
                {
                    Sender = "Slime",
                    Text = $"⚠️ {error}",
                    IsUser = false,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                });

                ScrollToBottom();
            });
        }

        private void OnAiUserMessageReceived(string thread, string prompt, string source)
        {
            if (source == "ChatWindow") return; // already added locally

            Dispatcher.Invoke(() =>
            {
                if (thread.Equals(CurrentThread, StringComparison.OrdinalIgnoreCase))
                {
                    Messages.Add(new ChatUiMessage
                    {
                        Sender = "You",
                        Text = prompt,
                        IsUser = true,
                        Timestamp = DateTime.Now.ToString("HH:mm")
                    });
                    ScrollToBottom();
                }
            });
        }

        private void UpdateBusyUI()
        {
            SendButton.IsEnabled = !IsBusy;
            StatusBanner.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ScrollToBottom()
        {
            ChatScrollViewer?.ScrollToEnd();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    SendMessage();
                }
            }
        }

        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PlaceholderText.Visibility = string.IsNullOrEmpty(InputTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void QuickChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cmd)
            {
                if (cmd.EndsWith(" "))
                {
                    InputTextBox.Text = cmd;
                    InputTextBox.Focus();
                    InputTextBox.CaretIndex = InputTextBox.Text.Length;
                }
                else
                {
                    InputTextBox.Text = cmd;
                    SendMessage();
                }
            }
        }

        private void ThreadComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThreadComboBox.SelectedItem is string selected && selected != CurrentThread)
            {
                CurrentThread = selected;
                UpdateThreadHint();
                LoadHistoryForThread(CurrentThread);
            }
        }

        private void ClearChat_Click(object sender, RoutedEventArgs e)
        {
            Messages.Clear();
            AddSystemMessage("Chat view cleared. (History is preserved in brain)");
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            PinButton.Content = Topmost ? "📌" : "📍";
            PinButton.Opacity = Topmost ? 1.0 : 0.6;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Keep window alive in background so conversation state stays intact
            e.Cancel = true;
            Hide();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
