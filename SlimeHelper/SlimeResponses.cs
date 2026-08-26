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

        public static readonly Dictionary<string, (string Error, string Semicolon, string Warning, string Idle)> SkinPhrases = new()
        {
            {
                "Pink",
                (
                    "Eww! My antennas feel {n} gross bug(s)!",
                    "Missing ; on line {line}! My bubbles are shaking!",
                    "Warning! {n} thing(s) are not very fabulous...",
                    "Just being pink and pretty! ✨"
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