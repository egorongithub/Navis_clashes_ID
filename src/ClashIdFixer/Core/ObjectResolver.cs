using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace ClashIdFixer.Core
{
    /// <summary>
    /// Finds "the real object" for a clash-result item: clash results point at a
    /// leaf geometry sub-object, while the object the user actually works with
    /// (the one whose properties show the "Объект"/Item tab, and the one Signal's
    /// parameter export keys off) is the composite-object level above it.
    /// </summary>
    public static class ObjectResolver
    {
        // Internal (language-independent) Navisworks property keys - NOT localized
        // display text, so they read the same under a Russian UI.
        private const string NodeTypeCategoryName = "LcOaNode";
        private const string NodeTypePropertyName = "LcOaNodeIcon";

        /// <summary>
        /// Walks from the item up through its parents and returns the nearest one
        /// that is an "object level" node, or null when there is none (e.g. plain
        /// AutoCAD geometry directly under a layer).
        /// </summary>
        public static ModelItem FindObjectLevel(ModelItem item, IList<string> fallbackNodeTypes)
        {
            for (var current = item; current != null; current = current.Parent)
            {
                if (IsObjectLevel(current, fallbackNodeTypes))
                    return current;
            }
            return null;
        }

        /// <summary>
        /// Primary check is ModelItem.IsComposite - a real API flag, immune to UI
        /// localization (comparing localized node-type names against English text
        /// is exactly the bug that produced "0 resolved" on a Russian install).
        /// The node-icon name comparison stays only as a configurable fallback for
        /// exotic files where the composite flag is not set.
        /// </summary>
        public static bool IsObjectLevel(ModelItem item, IList<string> fallbackNodeTypes)
        {
            try
            {
                if (item.IsComposite) return true;
            }
            catch
            {
            }

            if (fallbackNodeTypes == null || fallbackNodeTypes.Count == 0) return false;

            foreach (var name in GetNodeTypeNames(item))
            {
                if (fallbackNodeTypes.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// All known spellings of the item's node type: the (possibly localized)
        /// display name of the LcOaNodeIcon constant plus its raw ToString form.
        /// </summary>
        private static IEnumerable<string> GetNodeTypeNames(ModelItem item)
        {
            if (item == null) yield break;

            NamedConstant constant = null;
            try
            {
                var prop = item.PropertyCategories.FindPropertyByName(NodeTypeCategoryName, NodeTypePropertyName);
                if (prop != null) constant = prop.Value.ToNamedConstant();
            }
            catch
            {
            }

            if (constant == null) yield break;

            string displayName = null;
            try { displayName = constant.DisplayName; } catch { }
            if (!string.IsNullOrEmpty(displayName)) yield return displayName;

            string raw = null;
            try { raw = constant.ToString(); } catch { }
            if (!string.IsNullOrEmpty(raw) && raw != displayName) yield return raw;
        }
    }
}
