using System.IO;
using System.Text.RegularExpressions;

namespace SlimeHelper
{
    public sealed class PaperDollLayerPaths
    {
        public required string Base { get; init; }
        public string? EyeWhite { get; init; }
        public string? Eyes { get; init; }
        public string? EyesBlink { get; init; }
        public string? Nose { get; init; }
        public string? MouthClosed { get; init; }
        public string? MouthOpen { get; init; }
        public string? MouthSmile { get; init; }
        public string? Eyebrows { get; init; }
        public string? HairLeft { get; init; }
        public string? HairMid { get; init; }
        public string? HairRight { get; init; }
        public string? Overlay { get; init; }
        public List<string> OverlayFrames { get; init; } = [];
        public List<List<string>> MotionGroups { get; init; } = [];
    }

    /// <summary>
    /// Resolves Paper-Doll layer PNG file paths inside a &lt;State&gt;/Layers directory.
    /// Pure filesystem/string logic separated from WPF so it can be unit-tested in SlimeHelper.Checks.
    /// </summary>
    public static class PaperDollResolver
    {
        public static int ExtractTrailingNumber(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            var m = Regex.Match(stem, @"(\d+)$");
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
        }

        public static List<string> OrderPathsNaturally(IEnumerable<string> paths) =>
            paths.OrderBy(ExtractTrailingNumber)
                 .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                 .ToList();

        public static PaperDollLayerPaths? ResolveLayers(string stateDir)
        {
            if (string.IsNullOrWhiteSpace(stateDir)) return null;

            string layersDir = Path.Combine(stateDir, "Layers");
            if (!Directory.Exists(layersDir)) return null;

            var pngFiles = Directory.GetFiles(layersDir, "*.*")
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pngFiles.Count == 0) return null;

            string? FindLayer(params string[] candidateNames)
            {
                foreach (var name in candidateNames)
                {
                    var match = pngFiles.FirstOrDefault(f =>
                    {
                        string stem = Path.GetFileNameWithoutExtension(f);
                        return string.Equals(stem, name, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(stem, "01_" + name, StringComparison.OrdinalIgnoreCase);
                    });
                    if (match != null) return match;
                }
                return null;
            }

            string? basePath = FindLayer("base", "background merged", "background_merged");
            if (basePath == null) return null;

            var motionGroups = pngFiles
                .Select(f => new
                {
                    Path = f,
                    Match = Regex.Match(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), @"^slimemotion[-_](\d+)[-_](\d+)$")
                })
                .Where(x => x.Match.Success)
                .GroupBy(x => int.Parse(x.Match.Groups[1].Value))
                .OrderBy(g => g.Key)
                .Select(g => g.OrderBy(x => int.Parse(x.Match.Groups[2].Value))
                              .Select(x => x.Path)
                              .ToList())
                .Where(list => list.Count > 0)
                .ToList();

            var numberedEffects = pngFiles
                .Select(f => new
                {
                    Path = f,
                    Match = Regex.Match(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), @"^(?!\d+_|slimemotion[-_])([a-z][a-z0-9_-]*?)[-_](\d+)$")
                })
                .Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups[1].Value)
                .ToList();

            // Pick the primary multi-frame animated effect sequence (sorted numerically) so unrelated prefixes don't mix
            var primaryAnimatedGroup = numberedEffects
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            var overlayFrames = primaryAnimatedGroup != null
                ? primaryAnimatedGroup
                    .OrderBy(x => int.Parse(x.Match.Groups[2].Value))
                    .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Path)
                    .ToList()
                : new List<string>();

            var staticNumberedOverlay = numberedEffects
                .Where(g => g.Count() == 1)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First().Path)
                .FirstOrDefault();

            return new PaperDollLayerPaths
            {
                Base = basePath,
                EyeWhite = FindLayer("eye-white", "eyewhite", "sclera"),
                Eyes = FindLayer("eyes", "eyes-open", "eyes_open", "eyes-closed", "eyes_closed", "pupils"),
                EyesBlink = FindLayer("eyes-blink", "blink"),
                Nose = FindLayer("nose"),
                MouthClosed = FindLayer("mouth-closed", "mouth_closed", "moth-closed", "mouth"),
                MouthOpen = FindLayer("mouth-open", "mouth_open", "moth-open"),
                MouthSmile = FindLayer("mouth-smile", "mouth_smile"),
                Eyebrows = FindLayer("eyebrows"),
                HairLeft = FindLayer("hair-left", "hair_left"),
                HairMid = FindLayer("hair-mid", "hair_mid"),
                HairRight = FindLayer("hair-right", "hair_right"),
                Overlay = FindLayer("overlay") ?? staticNumberedOverlay,
                OverlayFrames = overlayFrames,
                MotionGroups = motionGroups
            };
        }
    }
}
