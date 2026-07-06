using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using ClashIdFixer.Config;

namespace ClashIdFixer.Core
{
    /// <summary>
    /// Result of resolving a clash-result ModelItem (which normally points at a
    /// leaf geometry sub-object) up to "the real object" - the composite-object
    /// level that has a proper "Item" property tab (shown as "Объект" in the
    /// Russian UI) and that other tools (e.g. Signal's parameter export) key off
    /// when the user does a normal (single-click) selection in Navisworks.
    /// </summary>
    public sealed class ResolvedObject
    {
        public ModelItem OriginalItem;
        public ModelItem ResolvedItem;
        public bool WasResolved;
        public string ResolvedNodeType;
    }

    public static class ObjectResolver
    {
        // These are internal (language-independent) Navisworks property keys, not
        // localized display text, so this works the same in a Russian-language
        // Navisworks UI as in an English one.
        private const string NodeTypeCategoryName = "LcOaNode";
        private const string NodeTypePropertyName = "LcOaNodeIcon";

        /// <summary>
        /// Returns the internal Navisworks node-type name for an item, e.g.
        /// "File", "Layer", "Collection", "Insert Group", "Composite Object", "Geometry".
        /// Returns null if it cannot be determined.
        /// </summary>
        public static string GetNodeTypeName(ModelItem item)
        {
            if (item == null) return null;
            try
            {
                var prop = item.PropertyCategories.FindPropertyByName(NodeTypeCategoryName, NodeTypePropertyName);
                if (prop == null) return null;
                return prop.Value.ToNamedConstant().DisplayName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Walks from <paramref name="clashItem"/> up through its ancestors (including
        /// itself) and returns the closest one whose node type is in
        /// <paramref name="acceptedNodeTypes"/>. Falls back to the original item
        /// (WasResolved = false) if nothing matched, so callers always get something
        /// usable and can flag unresolved rows instead of the export silently
        /// swallowing them.
        /// </summary>
        public static ResolvedObject ResolveObject(ModelItem clashItem, IList<string> acceptedNodeTypes)
        {
            var result = new ResolvedObject { OriginalItem = clashItem };

            if (clashItem != null)
            {
                foreach (ModelItem candidate in clashItem.AncestorsAndSelf)
                {
                    string nodeType = GetNodeTypeName(candidate);
                    if (nodeType != null && acceptedNodeTypes.Any(t => string.Equals(t, nodeType, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.ResolvedItem = candidate;
                        result.ResolvedNodeType = nodeType;
                        result.WasResolved = true;
                        return result;
                    }
                }
            }

            result.ResolvedItem = clashItem;
            result.WasResolved = false;
            return result;
        }

        /// <summary>Navisworks' own stable per-object GUID (ModelItem.InstanceGuid).</summary>
        public static string GetInstanceGuid(ModelItem item)
        {
            if (item == null) return null;
            try
            {
                return item.InstanceGuid.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Tries each configured (category, property) pair against the item's
        /// property categories and returns every one that actually resolved to a
        /// non-empty value. All of them are written to the export so the user can
        /// see which candidate matched their model without recompiling anything.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> ExtractIdCandidates(
            ModelItem item, IEnumerable<IdCandidate> candidates)
        {
            if (item == null) yield break;

            foreach (var c in candidates)
            {
                DataProperty prop = null;
                try
                {
                    prop = item.PropertyCategories.FindPropertyByDisplayName(c.Category, c.Property);
                }
                catch
                {
                    // Category/property not present on this item - just skip it.
                }

                if (prop == null) continue;

                string value = null;
                try
                {
                    value = prop.Value.ToDisplayString();
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(value))
                    yield return new KeyValuePair<string, string>(c.Key, value);
            }
        }
    }
}
