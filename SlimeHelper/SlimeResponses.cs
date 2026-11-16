namespace SlimeHelper
{
    public static class SlimeResponses
    {
        private static readonly Random _rnd = new();

        public static string PickRandom(IReadOnlyList<string> list)
        {
            if (list == null || list.Count == 0) return string.Empty;
            return list[_rnd.Next(list.Count)];
        }

        public static (string Status, string Message) PickIdleChatter(DateTime now, Random rng)
        {
            if (now.Hour is >= 23 or < 5 && rng.Next(0, 3) == 0)
            {
                return ("TIRED", "It's late. Slime is tired...");
            }
            if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 15 && rng.Next(0, 3) == 0)
            {
                return ("STREAK", "It's Friday! Friday! Yey!");
            }
            if (now.DayOfWeek == DayOfWeek.Monday && now.Hour < 9 && rng.Next(0, 3) == 0)
            {
                return ("TIRED", "Monday... need coffee...");
            }
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && rng.Next(0, 4) == 0)
            {
                return ("UNSURE", "Working on the weekend? Really?");
            }

            string randomThought = PickRandom(IdleThoughts);
            string[] randomReactions = { "IDLE", "CUTE", "THINKING", "HURRAY" };
            string selectedReaction = randomReactions[rng.Next(randomReactions.Length)];
            return (selectedReaction, randomThought);
        }

        /// <summary>
        /// Phase 4: Generates contextual, memory-aware, emoji-free idle thoughts based on active focus timers,
        /// time of day, open Markdown To-Do items, SlimeMemory nodes, and bound repositories.
        /// </summary>
        public static (string Status, string Message) PickContextualIdleChatter(
            DateTime now,
            Random rng,
            IReadOnlyList<string>? openTodos = null,
            IReadOnlyList<MemoryNode>? memoryNodes = null,
            IReadOnlyList<string>? boundRepoTags = null,
            (bool IsActive, int RemainingMinutes, string? TaskName)? focusStatus = null,
            (int Mood, int Energy)? moodState = null)
        {
            static string Shorten(string text, int maxLen = 65)
            {
                text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                return text.Length <= maxLen ? text : text[..(maxLen - 3)].TrimEnd() + "...";
            }

            // 1. Active Pomodoro / Focus session reminder
            if (focusStatus.HasValue && focusStatus.Value.IsActive && focusStatus.Value.RemainingMinutes > 0 && rng.Next(0, 2) == 0)
            {
                int mins = focusStatus.Value.RemainingMinutes;
                string? task = focusStatus.Value.TaskName;
                string msg = !string.IsNullOrWhiteSpace(task)
                    ? $"Stay focused on '{Shorten(task, 45)}'! About {mins} min left on the timer."
                    : $"Focus session in progress! About {mins} min remaining on the timer.";
                return ("NOTES", msg);
            }

            // 1b. Phase 5: Low energy or low mood idle reactions
            if (moodState.HasValue)
            {
                if (moodState.Value.Energy < 25 && rng.Next(0, 2) == 0)
                {
                    return ("TIRED", "Feeling a bit low on energy... a short break or coffee would feel nice.");
                }
                if (moodState.Value.Mood < 35 && rng.Next(0, 2) == 0)
                {
                    return ("UNSURE", "Rough session so far, but we can turn these bugs around!");
                }
            }

            // 2. Time-of-day reactions (late night, Friday afternoon, Monday morning, weekend)
            if (now.Hour is >= 23 or < 5 && rng.Next(0, 3) == 0)
            {
                return ("TIRED", "It's late. Slime is tired...");
            }
            if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 15 && rng.Next(0, 3) == 0)
            {
                return ("STREAK", "It's Friday! Friday! Yey!");
            }
            if (now.DayOfWeek == DayOfWeek.Monday && now.Hour < 9 && rng.Next(0, 3) == 0)
            {
                return ("TIRED", "Monday... need coffee...");
            }
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && rng.Next(0, 4) == 0)
            {
                return ("UNSURE", "Working on the weekend? Really?");
            }

            // 3. Open Markdown To-Do item reminder
            if (openTodos != null && openTodos.Count > 0 && rng.Next(0, 3) == 0)
            {
                string todo = Shorten(openTodos[rng.Next(openTodos.Count)], 60);
                string[] templates =
                {
                    $"By the way, we still have '{todo}' on our To-Do list.",
                    $"Don't forget about '{todo}' on your task list!",
                    $"Should we tackle '{todo}' next?"
                };
                return ("NOTES", templates[rng.Next(templates.Length)]);
            }

            // 4. Memory Bank node recall (SlimeMemory)
            if (memoryNodes != null && memoryNodes.Count > 0 && rng.Next(0, 3) == 0)
            {
                var candidates = memoryNodes.Where(n => !string.IsNullOrWhiteSpace(n.Content)).ToList();
                if (candidates.Count > 0)
                {
                    var node = candidates[rng.Next(candidates.Count)];
                    string snippet = Shorten(node.Content, 65);
                    if (!string.IsNullOrWhiteSpace(node.RelatedRepoOrTag))
                    {
                        return ("THINKING", $"Thinking about {node.RelatedRepoOrTag}: {snippet}");
                    }

                    string msg = node.Category switch
                    {
                        MemoryCategory.UserPreference => $"I remember you mentioned: {snippet}",
                        MemoryCategory.Architecture or MemoryCategory.ProjectDecision => $"Thinking about our design note: {snippet}",
                        _ => $"Just recalling a note from my memory bank: {snippet}"
                    };
                    return ("THINKING", msg);
                }
            }

            // 5. Bound Repository check-in
            if (boundRepoTags != null && boundRepoTags.Count > 0 && rng.Next(0, 4) == 0)
            {
                string tag = boundRepoTags[rng.Next(boundRepoTags.Count)];
                string[] repoTemplates =
                {
                    $"Making good progress in {tag} today!",
                    $"How are things going in {tag}?",
                    $"Ready whenever you need help with {tag}."
                };
                return ("CUTE", repoTemplates[rng.Next(repoTemplates.Length)]);
            }

            // 6. Fallback to standard idle thoughts (biased by Mood when high)
            string randomThought = PickRandom(IdleThoughts);
            string[] randomReactions = (moodState.HasValue && moodState.Value.Mood >= 80)
                ? new[] { "CUTE", "HURRAY", "IDLE", "CUTE" }
                : new[] { "IDLE", "CUTE", "THINKING", "HURRAY" };
            string selectedReaction = randomReactions[rng.Next(randomReactions.Length)];
            return (selectedReaction, randomThought);
        }

        public static readonly string[] ConsoleResponses = new[]
        {
            "Writing in the console are we?",
            "Log, log, log...",
            "Debugging via text? Classic!",
            "Console spam incoming!",
            "I see what you are logging...",
            "To the console! and beyoned!"
        };

        public static readonly string[] DebugResponses = new[]
        {
            "D-D-Debugging?",
            "Time to hunt some bugs",
            "Let's get this fixed",
            "I found what was wrong earlier, but forgot to tell you"
        };

        public static readonly string[] CoolResponses = new[]
        {
            "Oh yeah! Now we are talking!",
            "Hacker-man",
            "Hell yeah!"
        };

        public static readonly string[] SwearResponses = new[]
        {
            "Hey! No swearing!",
            "Watch your language!",
            "My ears! (If I had any)",
            "Keep it clean, coder!",
            "I can't say words like that..."
        };

        public static readonly string[] CommentResponses = new[]
        {
            "Yeah, let's comment that out",
            "Do you think anyone will read that?",
            "Code comments, nice!"
        };

        public static readonly string[] TodoResponses = new[]
        {
            "Maybe we should just do it now?",
            "Don't put off until tomorrow...",
            "Another TODO list item?",
            "Will you really fix this?",
            "Adding to the pile..."
        };

        public static readonly string[] FunnyResponses = new[]
        {
            "I have no idea what that does.",
            "Looks like magic to me.",
            "Are you sure about that?",
            "If it works, don't touch it!",
            "Not sure what to put there?"
        };

        public static readonly string[] CopyPasteResponses = new[]
        {
            "Vibe coding detected!",
            "Did you write ANY of this yourself?",
            "StackOverflow or ChatGPT?",
            "I know where you got that code from!",
            "Copying you own code or someone else?",
            "Did an AI write that?",
            "Are we coding or assembling?"
        };

        public static readonly string[] BreakResponses = new[]
        {
            "Maybe it's time for a break?",
            "Break time! strech those legs!",
            "Time for a break!",
            "You have been coding for almost an hour now",
            "Should we take a little break?",
            "Snack break!",
            "We should take a break"
        };

        public static readonly string[] IdleThoughts = new[]
        {
            "I wonder if I'm made of pixels or magic?",
            "have you tried turning it off and on again?",
            "I saw a bug!.. Just kidding",
            "Are we in the Matrix?",
            "So this is nice",
            "I like it when you scoll, It's like a rollercoaster",
            "Maybe we should commit so we have a backup?",
            "Slime is doing slime stuff",
            "How about we take a short break to clear our heads?",
            "So this code goes here.. and this string here...",
            "What if I just deleted this line of code?... joking!",
            "hum.. hum.. hum..",
            "I's all 1s and 0s",
            "Light theme attracts bugs. Stay in the dark.",
            "I hope the Garbage Collector doesn't delete me...",
            "My cousin is a Minecraft slime. He's square.",
            "Do I dream of electric sheeps?",
            "Tabs or Spaces? Don't answer, I judge.",
            "Is Chrome eating all our RAM again?",
            "I bet you missed a semicolon somewhere. Just a feeling.",
            "It works on my machine... because I live in it.",
            "First I help you code, then I take over the world...",
            "Are you sure you saved that file? Are you?",
            "I'm better than a rubber duck. I have personality.",
            "99 little bugs in the code... take one down... 127 bugs?!",
            "I tried to center a div once. I'm still traumatized.",
            "Please tell me you didn't push directly to main...",
            "It's getting warm in here. Is the CPU working hard?",
            "Posture check! Don't turn into a question mark.",
            "Recursion is cool. Recursion is cool. Recursion is...",
            "I don't read documentation. I guess and pray.",
            "There are 10 types of people. Those who know binary, and those who don't. 01",
            "More RGB lights equals more coding speed, right?",
            "I think your computer fan is trying to fly away.",
            "Mmm... spaghetti code. My favorite dish.",
            "Hydrate! Or you will dry out. I'm 90% water, so I know.",
            "Are we live on production? No? Phew.",
            "Ctrl+Z is the greatest invention in human history."
        };

        public static readonly string[] UncommittedResponses = new[]
        {
            "Psst! You have uncommitted changes just sitting there...",
            "Are we ever going to commit this code, or should it stay in limbo?",
            "Lots of modified files! Time for a commit?",
            "Don't let your changes pile up too much, commit them!",
            "Hey, check your git status. Uncommitted changes detected!"
        };

        public static readonly string[] UnpushedResponses = new[]
        {
            "You have commits that aren't pushed yet! Share them with the world!",
            "Local commits waiting to be pushed... Don't keep them to yourself!",
            "Time to run git push! Let's sync up.",
            "Your code is safe locally, but how about a push?",
            "Psst, you're ahead of origin! Push those commits!"
        };

        public static readonly string[] PokeAnnoyedResponses = new[]
        {
            "Hey! Stop poking me!",
            "Cut it out! That's enough pokes!",
            "Okay, okay, I get it! Stop poking!",
            "My patience is getting squishy...",
            "Again?! Give my pixels a rest!"
        };

        public static readonly string[] PokeDizzyResponses = new[]
        {
            "Whoa, I'm getting dizzy!!",
            "Everything is spinning! Stop poking!!",
            "Too many pokes! System overload!",
            "Mercy! I'm losing my shape!"
        };

        public static readonly string[] WindowShakeResponses = new[]
        {
            "Whoa! Earthquake!!",
            "Stop shaking me! I'm gonna spill!",
            "Wheee-wait, no, too fast!!",
            "My head is spinning! Easy on the window!"
        };

        public static readonly string[] CodingWindowResponses = new[]
        {
            "Deep in the code! Let's build something cool.",
            "Focus mode activated. What are we building?",
            "Back to the editor! Let's crush some bugs.",
            "Code editor open and ready. You've got this!"
        };

        public static readonly string[] MusicWindowResponses = new[]
        {
            "Ooh, good tunes! Music makes coding twice as fast.",
            "Picking out a good coding playlist?",
            "Turn up the volume, let's vibe and code!"
        };

        public static readonly string[] DistractionWindowResponses = new[]
        {
            "Taking a short break, or are we procrastinating?",
            "Wait a minute... this doesn't look like a code editor!",
            "Getting distracted? Don't forget what we were working on!",
            "A little break is fine, just don't fall down a rabbit hole!"
        };

        public static readonly string[] HighCpuResponses = new[]
        {
            "Whoa, heavy build! Hang in there, CPU!",
            "It's getting warm in here... the CPU is working really hard!",
            "Crunching numbers at full speed! What are we compiling?"
        };

        public static readonly string[] MeetingWindowResponses = new[]
        {
            "Meeting time! Don't forget to check if your mic is muted.",
            "Smile for the camera! I'll keep quiet while you're in the meeting.",
            "Another meeting? Could this one have been an email?",
            "In a call! Let me know if we need to take any notes."
        };

        public static readonly string[] EmailWindowResponses = new[]
        {
            "Checking emails? Hope it's good news and not spam!",
            "Inbox zero is a myth, but let's clear out the important ones.",
            "Reading mail! Don't click any suspicious links."
        };

        public static readonly string[] ChatCommsWindowResponses = new[]
        {
            "Chatting with the team? Tell them Slime says hello!",
            "Checking messages! Any spicy memes or just work talk?",
            "Catching up on chat? Don't let the notifications overwhelm you."
        };

        public static readonly string[] ApiTestingWindowResponses = new[]
        {
            "Testing endpoints! Fingers crossed for a 200 OK.",
            "Firing off API requests! Please no 500 Internal Server Error...",
            "Checking payloads and headers! Did the endpoint respond?"
        };

        public static readonly string[] DatabaseWindowResponses = new[]
        {
            "Working with the database! Remember: never run DELETE without a WHERE clause.",
            "Querying data! Watch out for slow joins.",
            "Inspecting tables and schemas! Keep those migrations clean."
        };

        public static readonly string[] GitHubActionsWindowResponses = new[]
        {
            "Watching the pipeline run! Come on, all green checkmarks please...",
            "Checking GitHub Actions! Nothing beats a passing workflow.",
            "Staring at the CI build logs? I'm crossing my fingers for you!"
        };

        public static readonly string[] GitHubWindowResponses = new[]
        {
            "Browsing GitHub! Reviewing pull requests or hunting issues?",
            "Checking the repo on GitHub! Ready to merge?",
            "Looking at commits and branches! Keep that commit history tidy."
        };

        public static readonly string[] DeployCloudWindowResponses = new[]
        {
            "Checking Vercel! Are we shipping to production right now?",
            "Looking at deployments! Hope the preview build looks crisp.",
            "Cloud dashboard open! Ready to go live?"
        };

        public static readonly string[] TerminalWindowResponses = new[]
        {
            "Command line mode! Real hackers live in the terminal.",
            "Running shell commands! Just be careful with rm -rf.",
            "In the terminal! Scripting away at full speed."
        };

        public static readonly string[] BrowserWindowResponses = new[]
        {
            "Researching in the browser? Let me know if you want me to look something up!",
            "Reading documentation or searching for answers? You'll find it!",
            "Surfing the web! Watch out so the browser doesn't eat all our RAM."
        };

        /// <summary>
        /// Classifies the foreground process name and window title into a context category.
        /// Specific web apps and IDE panels are matched before generic editors or browsers.
        /// </summary>
        public static string? ClassifyActiveWindow(string? processName, string? windowTitle)
        {
            string proc = (processName ?? "").Trim().ToLowerInvariant();
            string title = (windowTitle ?? "").Trim().ToLowerInvariant();

            if (proc is "slimehelper" or "slime" or "explorer" or "searchhost" or "shellexperiencehost" or "applicationframehost")
            {
                return null;
            }

            // 1. Meetings (Teams, Google Meet, Zoom)
            if (proc is "ms-teams" or "teams" or "zoom" ||
                title.Contains("google meet") || title.Contains("meet.google.com") ||
                title.Contains("microsoft teams") || title.Contains("zoom meeting"))
            {
                return "MEETING";
            }

            // 2. Email (Gmail, Outlook, Thunderbird)
            if (proc is "olk" or "outlook" or "thunderbird" ||
                title.Contains("gmail") || title.Contains("inbox -") || title.Contains("outlook"))
            {
                return "EMAIL";
            }

            // 3. Team & Community Chat (Slack, Discord)
            if (proc is "slack" or "discord" ||
                title.Contains("slack") || title.Contains("discord"))
            {
                return "CHAT_COMMS";
            }

            // 4. API Testing (Postman, Thunder Client, Insomnia, Bruno) - checked before CODING so Thunder Client inside VS Code matches here
            if (proc is "postman" or "insomnia" or "bruno" ||
                title.Contains("postman") || title.Contains("thunder client") ||
                title.Contains("thunderclient") || title.Contains("insomnia"))
            {
                return "API_TESTING";
            }

            // 5. Database tools (DBeaver, MySQL, pgAdmin, SSMS, DataGrip)
            if (proc is "dbeaver" or "mysql" or "mysqlworkbench" or "ssms" or "datagrip64" or "pgadmin4" ||
                title.Contains("dbeaver") || title.Contains("mysql") ||
                title.Contains("postgresql") || title.Contains("pgadmin"))
            {
                return "DATABASE";
            }

            // 6. GitHub Actions / CI Pipelines (checked before generic GitHub)
            if (title.Contains("github actions") || title.Contains("actions ·") ||
                title.Contains("workflow runs") || (title.Contains("github") && title.Contains("actions")))
            {
                return "GITHUB_ACTIONS";
            }

            // 7. GitHub (Repos, PRs, Issues, GitHub Desktop)
            if (proc is "githubdesktop" ||
                title.Contains("github") || title.Contains("pull request") || title.Contains("pull requests"))
            {
                return "GITHUB";
            }

            // 8. Cloud & Deployments (Vercel, Netlify, Railway, Cloudflare)
            if (title.Contains("vercel") || title.Contains("netlify") ||
                title.Contains("railway") || title.Contains("cloudflare"))
            {
                return "DEPLOY_CLOUD";
            }

            // 9. Terminals & Shells (Bash, Windows Terminal, PowerShell, CMD, etc.)
            if (proc is "windowsterminal" or "wt" or "powershell" or "pwsh" or "cmd" or "bash" or "sh" or "zsh" or "mintty" or "alacritty" or "wezterm-gui" ||
                title.Contains("git bash") || title.Contains("mingw64") ||
                title.Contains("windows powershell") || title.Contains("command prompt"))
            {
                return "TERMINAL";
            }

            // 10. Music apps and music tabs (checked before YouTube distraction so YouTube Music counts as music)
            if (proc is "spotify" or "musicbee" or "foobar2000" or "itunes" or "tidal" or "aimp" ||
                title.Contains("youtube music") || title.Contains("spotify"))
            {
                return "MUSIC";
            }

            // 11. Distraction / entertainment apps and sites
            if (proc is "steam" or "epicgameslauncher" or "battle.net" ||
                title.Contains("youtube") || title.Contains("reddit") || title.Contains("twitch") ||
                title.Contains("netflix") || title.Contains("tiktok") || title.Contains("9gag"))
            {
                return "DISTRACTION";
            }

            // 12. Coding editors and IDEs
            if (proc is "code" or "devenv" or "rider64" or "idea64" or "pycharm64" or "webstorm64" or "clion64" or "cursor" or "windsurf" or "nvim" or "vim" ||
                title.Contains("visual studio code") || title.Contains("microsoft visual studio") || title.Contains("jetbrains rider"))
            {
                return "CODING";
            }

            // 13. Web Browsers (Chrome, Firefox, Brave, Edge, etc. when not matching a specific web app above)
            if (proc is "chrome" or "firefox" or "brave" or "msedge" or "opera" or "vivaldi" or "arc")
            {
                return "BROWSER";
            }

            return null;
        }

        /// <summary>
        /// Returns an emoji-free status and spoken reaction for a given context category.
        /// </summary>
        public static (string Status, string Message)? GetContextReaction(string? category, Random rng)
        {
            if (string.IsNullOrWhiteSpace(category)) return null;

            return category.ToUpperInvariant() switch
            {
                "CODING" => ("NOTES", CodingWindowResponses[rng.Next(CodingWindowResponses.Length)]),
                "MUSIC" => ("CUTE", MusicWindowResponses[rng.Next(MusicWindowResponses.Length)]),
                "DISTRACTION" => ("FUNNY", DistractionWindowResponses[rng.Next(DistractionWindowResponses.Length)]),
                "HIGH_CPU" => ("TIRED", HighCpuResponses[rng.Next(HighCpuResponses.Length)]),
                "MEETING" => ("CUTE", MeetingWindowResponses[rng.Next(MeetingWindowResponses.Length)]),
                "EMAIL" => ("NOTES", EmailWindowResponses[rng.Next(EmailWindowResponses.Length)]),
                "CHAT_COMMS" => ("FUNNY", ChatCommsWindowResponses[rng.Next(ChatCommsWindowResponses.Length)]),
                "API_TESTING" => ("THINKING", ApiTestingWindowResponses[rng.Next(ApiTestingWindowResponses.Length)]),
                "DATABASE" => ("WARNING", DatabaseWindowResponses[rng.Next(DatabaseWindowResponses.Length)]),
                "GITHUB_ACTIONS" => ("STREAK", GitHubActionsWindowResponses[rng.Next(GitHubActionsWindowResponses.Length)]),
                "GITHUB" => ("NOTES", GitHubWindowResponses[rng.Next(GitHubWindowResponses.Length)]),
                "DEPLOY_CLOUD" => ("HURRAY", DeployCloudWindowResponses[rng.Next(DeployCloudWindowResponses.Length)]),
                "TERMINAL" => ("STREAK", TerminalWindowResponses[rng.Next(TerminalWindowResponses.Length)]),
                "BROWSER" => ("THINKING", BrowserWindowResponses[rng.Next(BrowserWindowResponses.Length)]),
                _ => null
            };
        }

        public static string[] GetBasePokePhrases(string skin) => skin switch
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

        /// <summary>
        /// Evaluates consecutive pokes and returns the appropriate emotion state, phrase, impact strength, and particle symbol.
        /// </summary>
        public static (string Status, string Message, double Impact, string Emote) GetPokeComboReaction(int pokeStreak, string skin, Random rng)
        {
            if (pokeStreak >= 5)
            {
                string msg = PokeDizzyResponses[rng.Next(PokeDizzyResponses.Length)];
                return ("SCARED", msg, 2.8, "💫");
            }
            if (pokeStreak >= 3)
            {
                string msg = PokeAnnoyedResponses[rng.Next(PokeAnnoyedResponses.Length)];
                return ("ANNOYED", msg, 2.1, "💢");
            }

            string[] basePhrases = GetBasePokePhrases(skin);
            string baseMsg = basePhrases[rng.Next(basePhrases.Length)];
            string emote = skin == "Pink" ? "💖" : "💧";
            return ("POKE", baseMsg, 1.6, emote);
        }

        public static readonly Dictionary<string, (string Error, string Semicolon, string Warning, string Idle)> SkinPhrases = new()
        {
            {
                "Pink",
                (
                    "Eww! My antennas feel {n} gross bug(s)!",
                    "Missing ; on line {line}! My bubbles are shaking!",
                    "Warning! {n} thing(s) are not very fabulous...",
                    "Just being pink and pretty!"
                )
            },
            {
                "Green",
                (
                    "Acid leak! {n} error(s) making me unstable...",
                    "I'm melting... Missing ; on line {line}!",
                    "Warning... {n} alert(s) detected...",
                    "Staying gooey..."
                )
            },
            {
                "Girl",
                (
                    "We have a {n} problem(s mister)",
                    "We missed a ; on line {line}!",
                    "Somethings wrong here! {n} error(s)",
                    "No problem! We got this!"
                )
            },
            {
                "Default",
                (
                    "You have {n} error(s) in your code!",
                    "Missing ; on line {line}!",
                    "You have {n} warning(s)!",
                    "Coding along with you!"
                )
            }
        };
    }
}