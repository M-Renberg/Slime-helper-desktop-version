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
        private readonly GitHubWatcher? _gitHubWatcher;
        private readonly GitHubService _gitHubService;

        public ObservableCollection<ChatUiMessage> Messages { get; } = new();

        public string CurrentThread { get; private set; } = "General";
        private ChatUiMessage? _streamingMessage;

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

        public ChatWindow(MainWindow mainWindow, SlimeBrainManager brainManager, CalendarWatcher? calendarWatcher = null, GitHubWatcher? gitHubWatcher = null)
        {
            InitializeComponent();
            DataContext = this;

            _mainWindow = mainWindow;
            _brainManager = brainManager;
            _calendarWatcher = calendarWatcher;
            _gitHubWatcher = gitHubWatcher;
            _gitHubService = gitHubWatcher?.Service ?? new GitHubService();

            // Subscribe to MainWindow AI pipeline events
            _mainWindow.AiStatusChanged += OnAiStatusChanged;
            _mainWindow.AiStreamChunkReceived += OnAiStreamChunkReceived;
            _mainWindow.AiResponseReceived += OnAiResponseReceived;
            _mainWindow.AiErrorOccurred += OnAiErrorOccurred;
            _mainWindow.AiUserMessageReceived += OnAiUserMessageReceived;
            _mainWindow.FocusTimerCompleted += msg => Dispatcher.Invoke(() => AddSystemMessage($"⏱️ {msg}"));
            _mainWindow.MoodChanged += mem => Dispatcher.Invoke(() => UpdateMoodUI(mem));

            UpdateMoodUI(_mainWindow.GetCurrentMoodMemory());
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

        private void UpdateMoodUI(SlimeMemory memory)
        {
            if (MoodStatusText == null || memory == null) return;
            MoodStatusText.Text = $"{memory.GetMoodLabel()} {memory.Mood}% • Energy {memory.Energy}%";
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
            _mainWindow.ProcessAiRequest(input, workingDir: workingDir, requestedThread: CurrentThread, source: "ChatWindow");
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
                    "• /todo — List all open '- [ ]' To-Do items from slime_notes.md and Obsidian\n" +
                    "• /todo <task> — Add a new '- [ ]' task to slime_notes.md\n" +
                    "• /todo done <number> — Mark task #<number> as '- [x]' completed\n" +
                    "• /focus <minutes> [task] — Start a Pomodoro focus timer (or /focus stop)\n" +
                    "• /mood [rest] — Show Slime's current Mood & Energy (or give her a coffee/rest boost)\n" +
                    "• /lang [Auto|Swedish|English] — View or change Slime's reply language mode\n" +
                    "• /skin <color> — Change skin (Blue/Default, Green, Pink, Girl)\n" +
                    "• /cal <event> — Add event to Google Calendar (e.g. /cal Meeting tomorrow at 14)\n" +
                    "• /notes — Open Obsidian or workspace notes\n" +
                    "• /bind <#tag> [path] — Bind a repository folder to a hashtag\n" +
                    "• /gh status — Show GitHub connection, current user, rate limits, and detected repo\n" +
                    "• /gh runs [branch] — View recent GitHub Actions workflow runs\n" +
                    "• /gh log <run_id> — View failure logs for a workflow run\n" +
                    "• /pr <title> [branch] — Create a GitHub Pull Request to main\n\n" +
                    "✨ You can also mention #repo or @topic anytime in normal questions!");
                return;
            }

            if (lower == "/clear")
            {
                _streamingMessage = null;
                Messages.Clear();
                AddSystemMessage("Chat view cleared. (History is safely kept in Slime's memory bank)");
                return;
            }

            if (lower.StartsWith("/lang"))
            {
                var settings = _mainWindow.LoadFullSettings();
                var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    string mode = parts[1].Trim();
                    string normalized = mode.ToLowerInvariant() switch
                    {
                        "sv" or "swe" or "swedish" or "svenska" => "Swedish",
                        "en" or "eng" or "english" or "engelska" => "English",
                        _ => "Auto"
                    };
                    settings.ResponseLanguage = normalized;
                    _mainWindow.SaveFullSettings(settings);
                    AddSystemMessage($"🌐 Reply language set to: {normalized}");
                }
                else
                {
                    AddSystemMessage($"🌐 Current reply language: {settings.ResponseLanguage} (Usage: /lang <Auto | Swedish | English>)");
                }
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
                var settings = _mainWindow.LoadFullSettings();
                var scanFolders = _brainManager.GetTodoScanFolders(CurrentThread, settings.ObsidianVaultPath);
                var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                string arg = parts.Length > 1 ? parts[1].Trim() : "";

                if (string.IsNullOrEmpty(arg) || arg.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    var items = NotesManager.ListUnfinishedTodos(scanFolders);
                    if (items.Count == 0)
                    {
                        AddSystemMessage("📝 No open To-Do items found! Add one with `/todo <task>`.");
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"📝 Open To-Do Items ({items.Count}):\n");
                        foreach (var item in items)
                        {
                            string fileLabel = Path.GetFileName(item.FilePath);
                            sb.AppendLine($"{item.Index}. [ ] {item.Text}  ({fileLabel})");
                        }
                        sb.AppendLine("\nTip: Complete a task with `/todo done <number>`.");
                        AddSystemMessage(sb.ToString().TrimEnd());
                    }
                    return;
                }

                if (arg.StartsWith("done ", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("check ", StringComparison.OrdinalIgnoreCase))
                {
                    string numPart = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "";
                    if (int.TryParse(numPart.TrimStart('#'), out int index))
                    {
                        var (ok, completedText, err) = NotesManager.CompleteTodo(index, scanFolders);
                        if (ok)
                        {
                            _mainWindow.CelebrateTodoCompleted(completedText);
                            AddSystemMessage($"✔ Marked #{index} as done (- [x]): \"{completedText}\"");
                        }
                        else
                        {
                            AddSystemMessage($"❌ {err}");
                        }
                    }
                    else
                    {
                        AddSystemMessage("Usage: /todo done <task_number> (run `/todo` to see numbers)");
                    }
                    return;
                }

                try
                {
                    string targetFolder = _brainManager.ResolveTodoFolder(CurrentThread);
                    string notesPath = NotesManager.AddTodo(targetFolder, arg);
                    string cleanTask = arg.StartsWith("add ", StringComparison.OrdinalIgnoreCase) && arg.Length > 4 ? arg[4..].Trim() : arg;
                    _mainWindow.NotifyTodoAdded(cleanTask);
                    AddSystemMessage($"✔ Added to To-Do: \"{cleanTask}\" (in {Path.GetFileName(notesPath)})");
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"❌ Could not save To-Do: {ex.Message}");
                }
                return;
            }

            if (lower.StartsWith("/focus"))
            {
                var parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    var (isActive, remaining, taskName) = _mainWindow.GetFocusStatus();
                    if (isActive)
                    {
                        string taskSuffix = !string.IsNullOrEmpty(taskName) ? $" on \"{taskName}\"" : "";
                        AddSystemMessage($"⏱️ Focus timer active{taskSuffix}: {(int)remaining.TotalMinutes}m {remaining.Seconds}s remaining.");
                    }
                    else
                    {
                        AddSystemMessage("⏱️ No focus timer running. Start one with `/focus <minutes> [optional task]` (e.g. `/focus 25 Fix login`).");
                    }
                    return;
                }

                if (parts[1].Equals("stop", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    string stopMsg = _mainWindow.StopFocusTimer();
                    AddSystemMessage($"⏱️ {stopMsg}");
                    return;
                }

                if (int.TryParse(parts[1], out int minutes))
                {
                    string? taskName = parts.Length > 2 ? parts[2] : null;
                    var (started, msg) = _mainWindow.StartFocusTimer(minutes, taskName);
                    AddSystemMessage(started ? $"⏱️ {msg}" : $"❌ {msg}");
                }
                else
                {
                    AddSystemMessage("Usage: `/focus <minutes> [task]` or `/focus stop`");
                }
                return;
            }

            if (lower.StartsWith("/mood"))
            {
                var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && (parts[1].Equals("rest", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("coffee", StringComparison.OrdinalIgnoreCase)))
                {
                    _mainWindow.RecordMoodEvent("REST");
                    var updated = _mainWindow.GetCurrentMoodMemory();
                    UpdateMoodUI(updated);
                    AddSystemMessage($"☕ Gave Slime a rest & coffee boost! {updated.GetStatusSummary()}");
                    return;
                }

                var mem = _mainWindow.GetCurrentMoodMemory();
                UpdateMoodUI(mem);
                AddSystemMessage($"💖 {mem.GetStatusSummary()}\nTip: Complete tasks (`/todo done`), pass CI builds, or run `/mood rest` to boost Slime's mood and energy!");
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

            if (lower == "/gh" || lower == "/gh status")
            {
                AddSystemMessage("🐙 Checking GitHub status...");
                string? token = GetGitHubToken();
                var (ok, user, rateRemaining) = await _gitHubService.TestConnectionAsync(token);
                var repoInfo = GetActiveRepo();
                string repoText = repoInfo.HasValue
                    ? $"{repoInfo.Value.owner}/{repoInfo.Value.repo} (at {GetActiveRepoPath()})"
                    : "No GitHub repository detected";
                var watched = _gitHubWatcher?.WatchedRepos;
                string watchedLine = watched != null && watched.Count > 1
                    ? $"\n• Watched Repositories ({watched.Count}): {string.Join(", ", watched.Select(r => $"{r.owner}/{r.repo}"))}"
                    : "";

                if (ok)
                {
                    string authSource = !string.IsNullOrWhiteSpace(token) ? "Configured PAT" : "GitHub CLI (`gh auth token`)";
                    AddSystemMessage(
                        $"🐙 GitHub Connection Status:\n\n" +
                        $"• Authenticated User: @{user}\n" +
                        $"• Rate Limit Remaining: {rateRemaining} calls\n" +
                        $"• Auth Source: {authSource}\n" +
                        $"• Active Repository: {repoText}{watchedLine}\n\n" +
                        $"💡 Type `/gh runs` to inspect recent workflow runs or `/pr <title>` to create a pull request.");
                }
                else
                {
                    AddSystemMessage(
                        $"⚠️ GitHub Connection Failed:\n\n" +
                        $"• Status: {user}\n" +
                        $"• Active Repository: {repoText}\n\n" +
                        $"💡 Please configure your token via right-click Settings -> GitHub Assistant -> Set GitHub Token, or login via `gh auth login`.");
                }
                return;
            }

            if (lower.StartsWith("/gh runs"))
            {
                var repoInfo = GetActiveRepo();
                if (!repoInfo.HasValue)
                {
                    AddSystemMessage("❌ No GitHub repository detected for this workspace.\nMake sure you are in a git repository with a GitHub remote or set a repository folder in Settings.");
                    return;
                }

                var parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                string? branch = parts.Length > 2 ? parts[2] : null;

                string branchHint = branch != null ? $" on branch `{branch}`" : "";
                AddSystemMessage($"🚀 Fetching workflow runs for {repoInfo.Value.owner}/{repoInfo.Value.repo}{branchHint}...");

                try
                {
                    string? token = GetGitHubToken();
                    var runs = await _gitHubService.GetLatestRunsAsync(repoInfo.Value.owner, repoInfo.Value.repo, branch, limit: 5, token: token);

                    if (runs.Count == 0)
                    {
                        AddSystemMessage($"ℹ️ No GitHub Actions workflow runs found for {repoInfo.Value.owner}/{repoInfo.Value.repo}{branchHint}.");
                        return;
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"🚀 Recent Workflow Runs ({repoInfo.Value.owner}/{repoInfo.Value.repo}):\n");

                    foreach (var run in runs)
                    {
                        string icon = (run.Conclusion ?? "").ToLowerInvariant() switch
                        {
                            "success" => "✅",
                            "failure" => "❌",
                            "cancelled" => "⚠️",
                            "timed_out" => "⏰",
                            _ => (run.Status ?? "").ToLowerInvariant() switch
                            {
                                "in_progress" => "⏳",
                                "queued" => "🕒",
                                _ => "⚪"
                            }
                        };

                        string statusText = !string.IsNullOrEmpty(run.Conclusion) ? run.Conclusion : (run.Status ?? "unknown");
                        string shortSha = !string.IsNullOrEmpty(run.HeadSha) && run.HeadSha.Length > 7 ? run.HeadSha[..7] : (run.HeadSha ?? "");
                        string commitSummary = !string.IsNullOrEmpty(run.DisplayTitle) ? run.DisplayTitle : "No commit title";
                        if (commitSummary.Contains('\n')) commitSummary = commitSummary.Split('\n')[0];

                        sb.AppendLine($"{icon} Run #{run.Id}: {run.Name} ({run.HeadBranch})");
                        sb.AppendLine($"   Status: {statusText} | Commit: {shortSha} - {commitSummary}");
                        sb.AppendLine($"   URL: {run.HtmlUrl}");
                        if ((run.Conclusion ?? "").Equals("failure", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"   💡 View log: `/gh log {run.Id}`");
                        }
                        sb.AppendLine();
                    }

                    AddSystemMessage(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"❌ Error fetching workflow runs: {ex.Message}");
                }
                return;
            }

            if (lower.StartsWith("/gh log"))
            {
                var parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !long.TryParse(parts[2], out long targetId))
                {
                    AddSystemMessage("Usage: /gh log <run_id | job_id>\nExample: `/gh log 12345678`");
                    return;
                }

                var repoInfo = GetActiveRepo();
                if (!repoInfo.HasValue)
                {
                    AddSystemMessage("❌ No GitHub repository detected for this workspace.");
                    return;
                }

                AddSystemMessage($"📋 Fetching log output for ID #{targetId}...");

                try
                {
                    string? token = GetGitHubToken();
                    string? logSnippet = null;
                    string jobName = "";

                    // First try to treat targetId as a run ID: fetch its jobs
                    var jobs = await _gitHubService.GetRunJobsAsync(repoInfo.Value.owner, repoInfo.Value.repo, targetId, token);
                    if (jobs.Count > 0)
                    {
                        var failedJob = jobs.FirstOrDefault(j =>
                            string.Equals(j.Conclusion, "failure", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(j.Conclusion, "timed_out", StringComparison.OrdinalIgnoreCase)) ?? jobs[0];

                        jobName = $" (Job: \"{failedJob.Name}\")";
                        logSnippet = await _gitHubService.GetJobLogTailAsync(repoInfo.Value.owner, repoInfo.Value.repo, failedJob.Id, maxLines: 120, token: token);
                    }
                    else
                    {
                        // Fallback: treat targetId directly as a job ID
                        logSnippet = await _gitHubService.GetJobLogTailAsync(repoInfo.Value.owner, repoInfo.Value.repo, targetId, maxLines: 120, token: token);
                    }

                    if (string.IsNullOrWhiteSpace(logSnippet))
                    {
                        AddSystemMessage($"ℹ️ No log output found for #{targetId}. The logs may have expired or the run has no failed steps.");
                    }
                    else
                    {
                        string msg = $"📋 GitHub Actions Log for #{targetId}{jobName}:\n```\n{logSnippet.Trim()}\n```\n\n" +
                                     $"💡 Ask Slime: \"Explain why this build failed and how to fix it\"!";
                        AddSystemMessage(msg);
                    }
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"❌ Error retrieving logs: {ex.Message}");
                }
                return;
            }

            if (lower.StartsWith("/pr"))
            {
                var repoInfo = GetActiveRepo();
                if (!repoInfo.HasValue)
                {
                    AddSystemMessage("❌ No GitHub repository detected for this workspace.\nMake sure you are in a git repository with a GitHub remote.");
                    return;
                }

                string remainder = cmd.Length > 3 ? cmd[3..].Trim() : "";
                if (string.IsNullOrWhiteSpace(remainder))
                {
                    AddSystemMessage("Usage: /pr <title> [head_branch]\nExample: `/pr Add user authentication feature-login`");
                    return;
                }

                string title = remainder;
                string? headBranch = null;

                if (remainder.StartsWith("\""))
                {
                    int endQuote = remainder.IndexOf('"', 1);
                    if (endQuote > 1)
                    {
                        title = remainder.Substring(1, endQuote - 1).Trim();
                        string rest = remainder.Substring(endQuote + 1).Trim();
                        if (!string.IsNullOrEmpty(rest)) headBranch = rest;
                    }
                }
                else
                {
                    int lastSpace = remainder.LastIndexOf(' ');
                    if (lastSpace > 0)
                    {
                        string possibleBranch = remainder.Substring(lastSpace + 1).Trim();
                        if (possibleBranch.Contains('/') || possibleBranch.Contains('-') || possibleBranch.Contains('_'))
                        {
                            headBranch = possibleBranch;
                            title = remainder.Substring(0, lastSpace).Trim();
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(headBranch))
                {
                    headBranch = GetCurrentGitBranch(GetActiveRepoPath());
                }

                if (string.IsNullOrWhiteSpace(headBranch) || headBranch == "main" || headBranch == "master")
                {
                    if (string.IsNullOrWhiteSpace(headBranch))
                    {
                        AddSystemMessage("❌ Could not automatically detect current git branch.\nPlease specify the branch explicitly: `/pr <title> <head_branch>`");
                        return;
                    }

                    AddSystemMessage($"⚠️ Current branch is `{headBranch}`. You cannot create a Pull Request from `{headBranch}` into `{headBranch}`.\nPlease specify the feature branch: `/pr <title> <feature_branch>`");
                    return;
                }

                AddSystemMessage($"🔀 Creating Pull Request: \"{title}\" (`{headBranch}` → `main`)...");

                try
                {
                    string? token = GetGitHubToken();
                    var (success, message, url) = await _gitHubService.CreatePullRequestAsync(
                        repoInfo.Value.owner,
                        repoInfo.Value.repo,
                        title,
                        headBranch,
                        baseBranch: "main",
                        body: $"Created by Slime Assistant 🐙\nBranch: `{headBranch}`",
                        token: token);

                    if (success)
                    {
                        AddSystemMessage($"🎉 Pull Request created successfully!\n\n🔗 [{title}]({url})\nHead: `{headBranch}` → Base: `main`");
                    }
                    else
                    {
                        AddSystemMessage($"❌ Failed to create PR: {message}");
                    }
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"❌ Error creating PR: {ex.Message}");
                }
                return;
            }

            AddSystemMessage($"Unknown command: {cmd}. Type /help for available commands.");
        }

        public void DisplayWorkflowFailure(GitHubWorkflowRun run, string? logSnippet)
        {
            string shortSha = !string.IsNullOrEmpty(run.HeadSha) && run.HeadSha.Length > 7
                ? run.HeadSha[..7]
                : (run.HeadSha ?? "");
            string commitMsg = !string.IsNullOrEmpty(run.DisplayTitle) ? run.DisplayTitle : "No commit title";
            if (commitMsg.Contains('\n'))
                commitMsg = commitMsg.Split('\n')[0];

            string card = $"🚨 Workflow Run #{run.Id} (\"{run.Name}\") FAILED on `{run.HeadBranch}`\n\n" +
                          $"• Commit: {shortSha} - {commitMsg}\n" +
                          $"• Trigger: {run.Event}\n" +
                          $"• URL: {run.HtmlUrl}\n\n";

            if (!string.IsNullOrWhiteSpace(logSnippet))
            {
                card += $"📋 Failure Log Snippet:\n```\n{logSnippet.Trim()}\n```\n\n" +
                        $"💡 Ask Slime: \"Explain why this CI step failed and how to fix it\" or type /gh log {run.Id}";
            }
            else
            {
                card += $"💡 Type `/gh log {run.Id}` to view error logs, or ask Slime for help diagnosing!";
            }

            AddSystemMessage(card);
            ScrollToBottom();
        }

        private (string owner, string repo)? GetActiveRepo()
        {
            if (_gitHubWatcher?.CurrentRepo != null)
                return _gitHubWatcher.CurrentRepo;

            var repoMap = _brainManager.GetBoundRepos();
            if (repoMap.TryGetValue(CurrentThread, out var boundPath) && !string.IsNullOrWhiteSpace(boundPath))
            {
                var boundRepo = GitHubService.GetRepoInfo(boundPath);
                if (boundRepo.HasValue) return boundRepo;
            }

            if (!string.IsNullOrWhiteSpace(_gitHubWatcher?.ActiveRepoPath))
            {
                var watcherRepo = GitHubService.GetRepoInfo(_gitHubWatcher.ActiveRepoPath);
                if (watcherRepo.HasValue) return watcherRepo;
            }

            var cwdRepo = GitHubService.GetRepoInfo(Directory.GetCurrentDirectory());
            if (cwdRepo.HasValue) return cwdRepo;

            return GitHubService.GetRepoInfo(AppDomain.CurrentDomain.BaseDirectory);
        }

        private string GetActiveRepoPath()
        {
            var repoMap = _brainManager.GetBoundRepos();
            if (repoMap.TryGetValue(CurrentThread, out var boundPath) && !string.IsNullOrWhiteSpace(boundPath))
                return boundPath;

            if (!string.IsNullOrWhiteSpace(_gitHubWatcher?.ActiveRepoPath))
                return _gitHubWatcher.ActiveRepoPath;

            return Directory.GetCurrentDirectory();
        }

        private string? GetGitHubToken()
        {
            if (!string.IsNullOrWhiteSpace(_gitHubWatcher?.GitHubToken))
                return _gitHubWatcher.GitHubToken;

            var settings = _mainWindow.LoadFullSettings();
            if (!string.IsNullOrWhiteSpace(settings.GitHubToken))
                return settings.GitHubToken;

            return null;
        }

        private static string? GetCurrentGitBranch(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse --abbrev-ref HEAD",
                    WorkingDirectory = directory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return null;

                string branch = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(2000);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch) && branch != "HEAD")
                {
                    return branch;
                }
            }
            catch { }

            return null;
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
                if (_streamingMessage != null)
                {
                    Messages.Remove(_streamingMessage);
                    _streamingMessage = null;
                }
                StatusTextBlock.Text = status;
                StatusBanner.Visibility = Visibility.Visible;
                ScrollToBottom();
            });
        }

        private void OnAiStreamChunkReceived(string thread, string partialText)
        {
            if (string.IsNullOrWhiteSpace(partialText)) return;

            Dispatcher.Invoke(() =>
            {
                if (!thread.Equals(CurrentThread, StringComparison.OrdinalIgnoreCase)) return;

                if (_streamingMessage == null || !Messages.Contains(_streamingMessage))
                {
                    _streamingMessage = new ChatUiMessage
                    {
                        Sender = "Slime",
                        Text = partialText,
                        IsUser = false,
                        Timestamp = DateTime.Now.ToString("HH:mm")
                    };
                    Messages.Add(_streamingMessage);
                }
                else
                {
                    _streamingMessage.Text = partialText;
                }

                ScrollToBottom();
            });
        }

        private void OnAiResponseReceived(string thread, string response)
        {
            Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                StatusBanner.Visibility = Visibility.Collapsed;

                if (thread.Equals(CurrentThread, StringComparison.OrdinalIgnoreCase))
                {
                    if (_streamingMessage != null && Messages.Contains(_streamingMessage))
                    {
                        _streamingMessage.Text = response;
                        _streamingMessage = null;
                    }
                    else
                    {
                        Messages.Add(new ChatUiMessage
                        {
                            Sender = "Slime",
                            Text = response,
                            IsUser = false,
                            Timestamp = DateTime.Now.ToString("HH:mm")
                        });
                    }

                    ScrollToBottom();
                }
                else
                {
                    _streamingMessage = null;
                }
            });
        }

        private void OnAiErrorOccurred(string error)
        {
            Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                StatusBanner.Visibility = Visibility.Collapsed;

                if (_streamingMessage != null)
                {
                    Messages.Remove(_streamingMessage);
                    _streamingMessage = null;
                }

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
            Dispatcher.InvokeAsync(() => ChatScrollViewer?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
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

                var repoMap = _brainManager.GetBoundRepos();
                if (repoMap.TryGetValue(selected, out var boundPath))
                {
                    _gitHubWatcher?.SetActiveRepo(boundPath);
                }
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
