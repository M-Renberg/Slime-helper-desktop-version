using System.Text.Json.Serialization;

namespace SlimeHelper
{
    public enum MemoryCategory
    {
        Fact,
        UserPreference,
        ProjectDecision,
        Architecture,
        Concept,
        Rule
    }

    /// <summary>
    /// Represents a directed edge/connection between two memory nodes in the memory network.
    /// </summary>
    public class MemoryRelation
    {
        public string TargetKey { get; set; } = "";
        public string RelationType { get; set; } = "relates_to"; // relates_to, depends_on, replaces, part_of, decided_for
    }

    /// <summary>
    /// Represents an individual knowledge node inside Slime's memory network.
    /// </summary>
    public class MemoryNode
    {
        public string Key { get; set; } = "";
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public MemoryCategory Category { get; set; } = MemoryCategory.Fact;
        public string Content { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public string? RelatedRepoOrTag { get; set; }
        public int Importance { get; set; } = 3; // 1 (minor note) to 5 (critical core fact/rule)
        public List<MemoryRelation> Relations { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int AccessCount { get; set; } = 0;
    }

    public class SlimeMemory
    {
        public string UserName { get; set; } = "Master";
        public string CurrentProject { get; set; } = "Unknown";
        public List<string> Facts { get; set; } = new();
        public List<MemoryNode> Nodes { get; set; } = new();

        /// <summary>
        /// Adds or updates a memory node by key (case-insensitive).
        /// </summary>
        public MemoryNode AddOrUpdateNode(MemoryNode node)
        {
            if (string.IsNullOrWhiteSpace(node.Key))
            {
                node.Key = "node_" + Guid.NewGuid().ToString("N")[..8];
            }
            node.Key = NormalizeKey(node.Key);

            var existing = Nodes.FirstOrDefault(n => NormalizeKey(n.Key) == node.Key);
            if (existing != null)
            {
                if (Facts.Contains(existing.Content))
                {
                    Facts.Remove(existing.Content);
                }
                existing.Content = node.Content;
                if (!Facts.Contains(node.Content))
                {
                    Facts.Add(node.Content);
                }
                existing.Category = node.Category;
                existing.Importance = node.Importance;
                existing.RelatedRepoOrTag = node.RelatedRepoOrTag;
                existing.UpdatedAt = DateTime.UtcNow;

                // Merge tags
                foreach (var tag in node.Tags)
                {
                    string normalizedTag = NormalizeTag(tag);
                    if (!existing.Tags.Any(t => NormalizeTag(t) == normalizedTag))
                    {
                        existing.Tags.Add(normalizedTag);
                    }
                }

                // Merge relations
                foreach (var rel in node.Relations)
                {
                    string targetKey = NormalizeKey(rel.TargetKey);
                    var existingRel = existing.Relations.FirstOrDefault(r => NormalizeKey(r.TargetKey) == targetKey);
                    if (existingRel == null)
                    {
                        existing.Relations.Add(new MemoryRelation { TargetKey = targetKey, RelationType = rel.RelationType });
                    }
                    else
                    {
                        existingRel.RelationType = rel.RelationType;
                    }
                }

                return existing;
            }

            node.Tags = node.Tags.Select(NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            node.CreatedAt = DateTime.UtcNow;
            node.UpdatedAt = DateTime.UtcNow;
            Nodes.Add(node);

            // Keep Facts list in sync for backwards compatibility
            if (!Facts.Contains(node.Content))
            {
                Facts.Add(node.Content);
            }

            return node;
        }

        /// <summary>
        /// Removes a memory node and any relations pointing to it.
        /// </summary>
        public bool RemoveNode(string key)
        {
            string norm = NormalizeKey(key);
            var node = Nodes.FirstOrDefault(n => NormalizeKey(n.Key) == norm);
            if (node == null) return false;

            Nodes.Remove(node);
            Facts.Remove(node.Content);

            // Clean up relations pointing to this node
            foreach (var remaining in Nodes)
            {
                remaining.Relations.RemoveAll(r => NormalizeKey(r.TargetKey) == norm);
            }

            return true;
        }

        public MemoryNode? GetNode(string key)
        {
            string norm = NormalizeKey(key);
            var node = Nodes.FirstOrDefault(n => NormalizeKey(n.Key) == norm);
            if (node != null)
            {
                node.AccessCount++;
            }
            return node;
        }

        /// <summary>
        /// Connects two memory nodes with a directed relation edge.
        /// </summary>
        public bool ConnectNodes(string sourceKey, string targetKey, string relationType = "relates_to")
        {
            string srcNorm = NormalizeKey(sourceKey);
            string tgtNorm = NormalizeKey(targetKey);

            var source = Nodes.FirstOrDefault(n => NormalizeKey(n.Key) == srcNorm);
            var target = Nodes.FirstOrDefault(n => NormalizeKey(n.Key) == tgtNorm);

            if (source == null || target == null) return false;

            var existingRel = source.Relations.FirstOrDefault(r => NormalizeKey(r.TargetKey) == tgtNorm);
            if (existingRel != null)
            {
                existingRel.RelationType = relationType;
            }
            else
            {
                source.Relations.Add(new MemoryRelation
                {
                    TargetKey = tgtNorm,
                    RelationType = string.IsNullOrWhiteSpace(relationType) ? "relates_to" : relationType.Trim().ToLowerInvariant()
                });
            }

            source.UpdatedAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Searches memory nodes by keywords, tags, or repo filter.
        /// </summary>
        public List<MemoryNode> SearchNodes(string query, string? repoTag = null, int limit = 5)
        {
            if (Nodes.Count == 0) return new List<MemoryNode>();

            var tokens = (query ?? "").Split(new[] { ' ', ',', ';', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Where(t => t.Length > 2)
                                     .Select(t => t.ToLowerInvariant())
                                     .ToList();

            string? normRepo = !string.IsNullOrWhiteSpace(repoTag) ? NormalizeTag(repoTag) : null;

            var scored = new List<(MemoryNode Node, int Score)>();

            foreach (var node in Nodes)
            {
                // If repo filter is supplied, prefer or require matching repo (allow global nodes where RelatedRepoOrTag is null)
                if (normRepo != null && !string.IsNullOrEmpty(node.RelatedRepoOrTag)
                    && !string.Equals(NormalizeTag(node.RelatedRepoOrTag), normRepo, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int tokenScore = 0;
                string contentLower = node.Content.ToLowerInvariant();
                string keyLower = node.Key.ToLowerInvariant();

                foreach (var token in tokens)
                {
                    if (keyLower.Contains(token)) tokenScore += 4;
                    if (node.Tags.Any(t => t.ToLowerInvariant().Contains(token))) tokenScore += 3;
                    if (contentLower.Contains(token)) tokenScore += 2;
                }

                if (tokens.Count > 0 && tokenScore == 0)
                {
                    continue;
                }

                int score = tokenScore + Math.Clamp(node.Importance, 1, 5);

                if (!string.IsNullOrEmpty(normRepo) && !string.IsNullOrEmpty(node.RelatedRepoOrTag)
                    && string.Equals(NormalizeTag(node.RelatedRepoOrTag), normRepo, StringComparison.OrdinalIgnoreCase))
                {
                    score += 5;
                }

                scored.Add((node, score));
            }

            return scored.OrderByDescending(s => s.Score)
                         .Select(s => s.Node)
                         .Take(limit)
                         .ToList();
        }

        /// <summary>
        /// Returns a node along with all connected nodes in the memory network up to the specified depth.
        /// </summary>
        public List<MemoryNode> GetNetworkSubgraph(string rootKey, int depth = 1)
        {
            var result = new List<MemoryNode>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Key, int CurrentDepth)>();

            string start = NormalizeKey(rootKey);
            queue.Enqueue((start, 0));
            visited.Add(start);

            while (queue.Count > 0)
            {
                var (currentKey, currentDepth) = queue.Dequeue();
                var node = Nodes.FirstOrDefault(n => NormalizeKey(n.Key) == currentKey);
                if (node == null) continue;

                result.Add(node);

                if (currentDepth < depth)
                {
                    foreach (var rel in node.Relations)
                    {
                        string tgt = NormalizeKey(rel.TargetKey);
                        if (!visited.Contains(tgt))
                        {
                            visited.Add(tgt);
                            queue.Enqueue((tgt, currentDepth + 1));
                        }
                    }

                    // Also include reverse connections (nodes pointing to this node)
                    foreach (var other in Nodes)
                    {
                        if (other.Relations.Any(r => NormalizeKey(r.TargetKey) == currentKey))
                        {
                            string otherKey = NormalizeKey(other.Key);
                            if (!visited.Contains(otherKey))
                            {
                                visited.Add(otherKey);
                                queue.Enqueue((otherKey, currentDepth + 1));
                            }
                        }
                    }
                }
            }

            return result;
        }

        public static string NormalizeKey(string key)
        {
            return (key ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
        }

        public static string NormalizeTag(string tag)
        {
            tag = (tag ?? "").Trim().ToLowerInvariant();
            if (!tag.StartsWith("#")) tag = "#" + tag;
            return tag;
        }
    }
}

