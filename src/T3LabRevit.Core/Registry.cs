using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace T3LabRevit.Core
{
    internal enum ToolKind
    {
        /// <summary>Reads only: no transaction.</summary>
        Read,
        /// <summary>Changes the model: one Transaction = one Undo step; warnings dismissed, errors roll back.</summary>
        Write,
        /// <summary>Application level (documents, active view, selection): no transaction.</summary>
        App,
    }

    internal delegate object ToolHandler(ToolContext ctx, Args args);

    internal sealed class ToolContext
    {
        public UIApplication App;
        public UIDocument UiDoc;
        public Document Doc;
        /// <summary>Revit warnings dismissed during the call's transaction (reported back to the AI).</summary>
        public readonly List<string> Warnings = new List<string>();
    }

    internal sealed class ToolDef
    {
        public string Name, Title, Description;
        public Dictionary<string, object> Schema;
        public ToolKind Kind;
        public ToolHandler Handler;
        public bool NeedsDocument = true;
        /// <summary>Write tools only: false = the handler opens its own transaction(s).</summary>
        public bool Tx = true;
        public bool Destructive;
        /// <summary>Runs on the socket thread, never touches the Revit API (t3lab_narrate).</summary>
        public bool Background;
        /// <summary>A Read tool that can also write a file (CSV, image): not advertised as read-only.</summary>
        public bool WritesFiles;
    }

    /// <summary>Tool result rendered as an MCP image block plus a JSON text block.</summary>
    internal sealed class ImageResult
    {
        public string Base64;
        public string MimeType = "image/png";
        public object Info;
    }

    internal static class Registry
    {
        static Dictionary<string, ToolDef> _tools = new Dictionary<string, ToolDef>();
        static readonly List<string> Order = new List<string>();

        public static int Count => _tools.Count;

        public static IEnumerable<ToolDef> All => Order.Select(n => _tools[n]);

        public static void Build()
        {
            _tools = new Dictionary<string, ToolDef>(StringComparer.Ordinal);
            Order.Clear();
            StatusTools.Register();
            AgentCursors.Register();
            DocumentTools.Register();
            ViewTools.Register();
            QueryTools.Register();
            ParameterTools.Register();
            CreateTools.Register();
            ModifyTools.Register();
            AnnotationTools.Register();
            SheetTools.Register();
            ScheduleTools.Register();
            ExportTools.Register();
            ModelTools.Register();
        }

        public static void Add(string name, string title, ToolKind kind, string description, string schema, ToolHandler handler,
            bool needsDocument = true, bool tx = true, bool destructive = false, bool background = false, bool writesFiles = false)
        {
            if (!_tools.ContainsKey(name)) Order.Add(name);
            _tools[name] = new ToolDef
            {
                Name = name,
                Title = title,
                Kind = kind,
                Description = description,
                Schema = Json.Schema(schema),
                Handler = handler,
                NeedsDocument = needsDocument,
                Tx = tx && kind == ToolKind.Write,
                Destructive = destructive,
                Background = background,
                WritesFiles = writesFiles,
            };
        }

        public static ToolDef Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

        public static object ListJson()
        {
            var tools = All.Select(t => (object)new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["description"] = t.Description,
                ["inputSchema"] = t.Schema,
                ["annotations"] = new Dictionary<string, object>
                {
                    ["title"] = t.Title,
                    ["readOnlyHint"] = t.Kind == ToolKind.Read && !t.WritesFiles,
                    ["destructiveHint"] = t.Destructive,
                    ["openWorldHint"] = false,
                },
            }).ToList();
            return new Dictionary<string, object> { ["tools"] = tools };
        }
    }

    /// <summary>Helpers to write JSON schemas compactly (single quotes, no quotes inside descriptions).</summary>
    internal static class Sch
    {
        public static string Obj(params string[] props) => ObjR("", props);

        /// <summary>Object schema; required is a comma-separated list of property names.</summary>
        public static string ObjR(string required, params string[] props)
        {
            string req = string.IsNullOrEmpty(required)
                ? ""
                : ",'required':[" + string.Join(",", required.Split(',').Select(r => "'" + r.Trim() + "'")) + "]";
            return "{'type':'object','properties':{" + string.Join(",", props) + "}" + req + "}";
        }

        public static string P(string name, string schema) => "'" + name + "':" + schema;
        public static string Str(string d) => "{'type':'string','description':'" + d + "'}";
        public static string Num(string d) => "{'type':'number','description':'" + d + "'}";
        public static string Int(string d) => "{'type':'integer','description':'" + d + "'}";
        public static string Bool(string d) => "{'type':'boolean','description':'" + d + "'}";
        public static string Any(string d) => "{'description':'" + d + "'}";
        public static string Strs(string d) => "{'type':'array','items':{'type':'string'},'description':'" + d + "'}";
        public static string IdList(string d) => "{'type':'array','items':{'type':'integer'},'description':'" + d + "'}";
        public static string Pt(string d) => "{'type':'array','items':{'type':'number'},'minItems':2,'maxItems':3,'description':'" + d + "'}";
        public static string Pts(string d) => "{'type':'array','items':{'type':'array','items':{'type':'number'}},'description':'" + d + "'}";
        public static string Enum(string d, params string[] values) =>
            "{'type':'string','enum':[" + string.Join(",", values.Select(v => "'" + v + "'")) + "],'description':'" + d + "'}";
        public static string ObjAny(string d) => "{'type':'object','description':'" + d + "'}";
        public static string Arr(string d, string itemSchema) => "{'type':'array','items':" + itemSchema + ",'description':'" + d + "'}";

        public const string Ids = "'element_ids':{'type':'array','items':{'type':'integer'},'description':'Element ids (from query_elements, get_selection or a create tool)'}";
        public const string Limit = "'limit':{'type':'integer','description':'Max rows returned (default 200)'}";
        public const string Offset = "'offset':{'type':'integer','description':'Rows to skip, for paging'}";
    }
}
