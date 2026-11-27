using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace T3LabRevit.Core
{
    /// <summary>
    /// The models open in this Revit: the active one, others in windows, and ones opened in the background (no window:
    /// manage_document open background=true, or a new model kept hidden). Any tool that takes revit_document works in any
    /// of them without switching what the user sees. Main thread only.
    /// </summary>
    internal static class Documents
    {
        public static IEnumerable<Document> Open(UIApplication app) => app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked);

        /// <summary>By title (with or without .rvt), full path or file name; ToolError lists the open models when none matches.</summary>
        public static Document Find(UIApplication app, string titleOrPath)
        {
            string want = (titleOrPath ?? "").Trim().Trim('"');
            foreach (Document d in Open(app))
            {
                string path = d.PathName ?? "";
                if (string.Equals(d.Title, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(d.Title), want, StringComparison.OrdinalIgnoreCase) ||
                    (path.Length > 0 && (string.Equals(path, want, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileName(path), want, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileNameWithoutExtension(path), want, StringComparison.OrdinalIgnoreCase))))
                    return d;
            }
            throw new ToolError($"No open model '{titleOrPath}'. Open models: {string.Join(", ", Open(app).Select(d => d.Title))}. " +
                                "Open it first: manage_document action=open (background=true keeps it out of sight).");
        }

        /// <summary>Open without a window: the user does not see it, tools still work in it.</summary>
        public static bool IsBackground(Document d)
        {
            try { return new UIDocument(d).GetOpenUIViews().Count == 0; }
            catch (Exception) { return false; }
        }
    }
}
